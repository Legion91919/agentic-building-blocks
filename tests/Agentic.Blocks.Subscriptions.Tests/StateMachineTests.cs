using Agentic.Blocks.Core;
using Agentic.Blocks.Subscriptions;
using Xunit;

namespace Agentic.Blocks.Subscriptions.Tests;

public class StateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromDays(7);
    private static readonly Plan Monthly = new("plus", "Plus", Money.Of(9.99m, "EUR"), BillingInterval.Monthly, 0, new HashSet<string> { "hd" });
    private static readonly Plan Trial = new("family", "Family", Money.Of(14.99m, "EUR"), BillingInterval.Monthly, 7, new HashSet<string> { "hd", "family" });

    private static Subscription InState(SubscriptionStatus status, bool cancelAtEnd = false) => new()
    {
        Id = "s",
        UserId = "u",
        PlanId = "plus",
        Status = status,
        CurrentPeriodStart = T0,
        CurrentPeriodEnd = T0.AddMonths(1),
        PastDueSince = status == SubscriptionStatus.PastDue ? T0 : null,
        CancelAtPeriodEnd = cancelAtEnd,
    };

    // from, event, days after T0, cancel-at-period-end flag, expected status (null = conflict)
    [Theory]
    [InlineData(SubscriptionStatus.Trialing, SubscriptionEvent.PaymentSucceeded, 0, false, SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Trialing, SubscriptionEvent.PaymentFailed, 0, false, SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Trialing, SubscriptionEvent.CancelNow, 0, false, SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.Trialing, SubscriptionEvent.CancelAtPeriodEnd, 0, false, SubscriptionStatus.Trialing)]
    [InlineData(SubscriptionStatus.Trialing, SubscriptionEvent.TimePassed, 40, false, SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Trialing, SubscriptionEvent.TimePassed, 40, true, SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.Active, SubscriptionEvent.PaymentSucceeded, 0, false, SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Active, SubscriptionEvent.PaymentFailed, 0, false, SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Active, SubscriptionEvent.CancelNow, 0, false, SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.Active, SubscriptionEvent.CancelAtPeriodEnd, 0, false, SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Active, SubscriptionEvent.Resume, 0, true, SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Active, SubscriptionEvent.TimePassed, 10, false, SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Active, SubscriptionEvent.TimePassed, 31, false, SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Active, SubscriptionEvent.TimePassed, 31, true, SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.PastDue, SubscriptionEvent.PaymentSucceeded, 3, false, SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.PastDue, SubscriptionEvent.PaymentFailed, 3, false, SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.PastDue, SubscriptionEvent.CancelNow, 3, false, SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.PastDue, SubscriptionEvent.CancelAtPeriodEnd, 3, false, null)]
    [InlineData(SubscriptionStatus.PastDue, SubscriptionEvent.TimePassed, 6, false, SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.PastDue, SubscriptionEvent.TimePassed, 7, false, SubscriptionStatus.Expired)]
    [InlineData(SubscriptionStatus.Cancelled, SubscriptionEvent.PaymentSucceeded, 0, false, null)]
    [InlineData(SubscriptionStatus.Cancelled, SubscriptionEvent.Resume, 0, false, null)]
    [InlineData(SubscriptionStatus.Cancelled, SubscriptionEvent.TimePassed, 90, false, SubscriptionStatus.Cancelled)]
    [InlineData(SubscriptionStatus.Expired, SubscriptionEvent.PaymentSucceeded, 0, false, null)]
    [InlineData(SubscriptionStatus.Expired, SubscriptionEvent.CancelNow, 0, false, null)]
    public void Transition_table(SubscriptionStatus from, SubscriptionEvent e, int days, bool cancelAtEnd, SubscriptionStatus? expected)
    {
        var result = SubscriptionStateMachine.Apply(InState(from, cancelAtEnd), e, T0.AddDays(days), Monthly, Grace);
        if (expected is null)
        {
            Assert.Equal("subscriptions.invalid_transition", result.Error.Code);
            return;
        }

        Assert.Equal(expected.Value, result.Value.Status);
    }

    [Fact]
    public void Start_with_trial_is_trialing_until_trial_end()
    {
        var s = SubscriptionStateMachine.Start("s", "u", Trial, T0);
        Assert.Equal(SubscriptionStatus.Trialing, s.Status);
        Assert.Equal(T0.AddDays(7), s.TrialEndsAt);
        Assert.Equal(T0.AddDays(7), s.CurrentPeriodEnd);
    }

    [Fact]
    public void Start_without_trial_is_active_for_one_interval()
    {
        var s = SubscriptionStateMachine.Start("s", "u", Monthly, T0);
        Assert.Equal(SubscriptionStatus.Active, s.Status);
        Assert.Equal(T0.AddMonths(1), s.CurrentPeriodEnd);
        Assert.Null(s.TrialEndsAt);
    }

    [Fact]
    public void Early_renewal_extends_from_period_end()
    {
        var s = SubscriptionStateMachine.Apply(InState(SubscriptionStatus.Active), SubscriptionEvent.PaymentSucceeded, T0.AddDays(28), Monthly, Grace).Value;
        Assert.Equal(T0.AddMonths(1), s.CurrentPeriodStart);
        Assert.Equal(T0.AddMonths(2), s.CurrentPeriodEnd);
    }

    [Fact]
    public void Payment_after_past_due_starts_new_period_now_and_clears_past_due()
    {
        var s = SubscriptionStateMachine.Apply(InState(SubscriptionStatus.PastDue), SubscriptionEvent.PaymentSucceeded, T0.AddDays(3), Monthly, Grace).Value;
        Assert.Equal(T0.AddDays(3), s.CurrentPeriodStart);
        Assert.Null(s.PastDueSince);
    }

    [Fact]
    public void Advance_walks_active_to_past_due_to_expired_after_long_gap()
    {
        var s = SubscriptionStateMachine.Advance(InState(SubscriptionStatus.Active), T0.AddMonths(3), Monthly, Grace);
        Assert.Equal(SubscriptionStatus.Expired, s.Status);
        Assert.Equal(T0.AddMonths(1) + Grace, s.EndedAt);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Trialing, true, true)]
    [InlineData(SubscriptionStatus.Active, true, true)]
    [InlineData(SubscriptionStatus.PastDue, true, true)]
    [InlineData(SubscriptionStatus.PastDue, false, false)]
    [InlineData(SubscriptionStatus.Cancelled, true, false)]
    [InlineData(SubscriptionStatus.Expired, true, false)]
    public void Access_rules(SubscriptionStatus status, bool duringGrace, bool expected)
    {
        Assert.Equal(expected, SubscriptionStateMachine.GrantsAccess(InState(status), duringGrace));
    }
}
