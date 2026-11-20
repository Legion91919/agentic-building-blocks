using Agentic.Blocks.Core;

namespace Agentic.Blocks.Subscriptions;

/// <summary>Billing interval.</summary>
public enum BillingInterval
{
    /// <summary>One calendar month.</summary>
    Monthly,
    /// <summary>One calendar year.</summary>
    Yearly,
}

/// <summary>A plan users can subscribe to.</summary>
/// <param name="Id">Plan id, for example <c>plus</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="Price">Price per interval.</param>
/// <param name="Interval">Billing interval.</param>
/// <param name="TrialDays">Free trial days (0 for none).</param>
/// <param name="Entitlements">Features the plan unlocks, for example <c>hd</c>, <c>offline</c>, <c>family</c>.</param>
public sealed record Plan(string Id, string Name, Money Price, BillingInterval Interval, int TrialDays, IReadOnlySet<string> Entitlements)
{
    /// <summary>End of a period that starts at <paramref name="start"/>.</summary>
    public DateTimeOffset PeriodEnd(DateTimeOffset start) => Interval == BillingInterval.Yearly ? start.AddYears(1) : start.AddMonths(1);
}

/// <summary>Subscription states.</summary>
public enum SubscriptionStatus
{
    /// <summary>In a free trial.</summary>
    Trialing,
    /// <summary>Paid and current.</summary>
    Active,
    /// <summary>A payment failed or is overdue; in the grace period.</summary>
    PastDue,
    /// <summary>Ended by the user (terminal).</summary>
    Cancelled,
    /// <summary>Ended because payment never arrived (terminal).</summary>
    Expired,
}

/// <summary>Things that move a subscription between states.</summary>
public enum SubscriptionEvent
{
    /// <summary>The provider collected a payment.</summary>
    PaymentSucceeded,
    /// <summary>The provider failed to collect a payment.</summary>
    PaymentFailed,
    /// <summary>The user cancels now.</summary>
    CancelNow,
    /// <summary>The user cancels at the end of the current period.</summary>
    CancelAtPeriodEnd,
    /// <summary>The user withdraws a pending cancel-at-period-end.</summary>
    Resume,
    /// <summary>The clock moved (trial end, period end, grace end).</summary>
    TimePassed,
}

/// <summary>A user's subscription. Immutable; transitions return a new copy.</summary>
public sealed record Subscription
{
    /// <summary>Id.</summary>
    public required string Id { get; init; }

    /// <summary>Owner.</summary>
    public required string UserId { get; init; }

    /// <summary>Plan id.</summary>
    public required string PlanId { get; init; }

    /// <summary>Status.</summary>
    public required SubscriptionStatus Status { get; init; }

    /// <summary>When the current period (or trial) started.</summary>
    public required DateTimeOffset CurrentPeriodStart { get; init; }

    /// <summary>When the current period (or trial) ends.</summary>
    public required DateTimeOffset CurrentPeriodEnd { get; init; }

    /// <summary>End of the trial, if there was one.</summary>
    public DateTimeOffset? TrialEndsAt { get; init; }

    /// <summary>Since when payment is overdue.</summary>
    public DateTimeOffset? PastDueSince { get; init; }

    /// <summary>True when the user asked to stop at period end.</summary>
    public bool CancelAtPeriodEnd { get; init; }

    /// <summary>When it reached a terminal state.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Optimistic-concurrency version, maintained by the store.</summary>
    public long Version { get; init; }

    /// <summary>True for Cancelled and Expired.</summary>
    public bool IsTerminal => Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired;
}

/// <summary>Pure transition rules. No I/O, no clock: time comes in as <c>now</c>.</summary>
public static class SubscriptionStateMachine
{
    /// <summary>Starts a subscription after checkout: Trialing when the plan has a trial, otherwise Active.</summary>
    public static Subscription Start(string id, string userId, Plan plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var trial = plan.TrialDays > 0;
        var end = trial ? now.AddDays(plan.TrialDays) : plan.PeriodEnd(now);
        return new Subscription
        {
            Id = id,
            UserId = userId,
            PlanId = plan.Id,
            Status = trial ? SubscriptionStatus.Trialing : SubscriptionStatus.Active,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = end,
            TrialEndsAt = trial ? end : null,
        };
    }

    /// <summary>Applies an event. Returns the unchanged subscription for no-ops, or a conflict error for forbidden transitions.</summary>
    public static Result<Subscription> Apply(Subscription s, SubscriptionEvent e, DateTimeOffset now, Plan plan, TimeSpan gracePeriod)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(plan);
        if (s.IsTerminal)
        {
            return e == SubscriptionEvent.TimePassed ? s : Invalid(s, e);
        }

        return (s.Status, e) switch
        {
            (_, SubscriptionEvent.PaymentSucceeded) => Renew(s, now, plan),
            (SubscriptionStatus.PastDue, SubscriptionEvent.PaymentFailed) => s,
            (_, SubscriptionEvent.PaymentFailed) => s with { Status = SubscriptionStatus.PastDue, PastDueSince = now },
            (_, SubscriptionEvent.CancelNow) => s with { Status = SubscriptionStatus.Cancelled, EndedAt = now, CancelAtPeriodEnd = false },
            (SubscriptionStatus.PastDue, SubscriptionEvent.CancelAtPeriodEnd or SubscriptionEvent.Resume) => Invalid(s, e),
            (_, SubscriptionEvent.CancelAtPeriodEnd) => s with { CancelAtPeriodEnd = true },
            (_, SubscriptionEvent.Resume) => s with { CancelAtPeriodEnd = false },
            (SubscriptionStatus.PastDue, SubscriptionEvent.TimePassed) => now >= s.PastDueSince!.Value + gracePeriod
                ? s with { Status = SubscriptionStatus.Expired, EndedAt = s.PastDueSince.Value + gracePeriod }
                : s,
            (_, SubscriptionEvent.TimePassed) when now < s.CurrentPeriodEnd => s,
            (_, SubscriptionEvent.TimePassed) when s.CancelAtPeriodEnd => s with { Status = SubscriptionStatus.Cancelled, EndedAt = s.CurrentPeriodEnd },
            (_, SubscriptionEvent.TimePassed) => s with { Status = SubscriptionStatus.PastDue, PastDueSince = s.CurrentPeriodEnd },
            _ => Invalid(s, e),
        };
    }

    /// <summary>Applies <see cref="SubscriptionEvent.TimePassed"/> until the state is stable (for example Active to PastDue to Expired).</summary>
    public static Subscription Advance(Subscription s, DateTimeOffset now, Plan plan, TimeSpan gracePeriod)
    {
        for (var i = 0; i < 4; i++)
        {
            var next = Apply(s, SubscriptionEvent.TimePassed, now, plan, gracePeriod).Value;
            if (next == s) return s;
            s = next;
        }

        return s;
    }

    /// <summary>True when the subscription unlocks its plan's entitlements; <paramref name="duringGrace"/> decides for PastDue.</summary>
    public static bool GrantsAccess(Subscription s, bool duringGrace)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Status switch
        {
            SubscriptionStatus.Trialing or SubscriptionStatus.Active => true,
            SubscriptionStatus.PastDue => duringGrace,
            _ => false,
        };
    }

    private static Subscription Renew(Subscription s, DateTimeOffset now, Plan plan)
    {
        var start = s.Status == SubscriptionStatus.Active && now < s.CurrentPeriodEnd ? s.CurrentPeriodEnd : now;
        return s with
        {
            Status = SubscriptionStatus.Active,
            PlanId = plan.Id,
            CurrentPeriodStart = start,
            CurrentPeriodEnd = plan.PeriodEnd(start),
            PastDueSince = null,
        };
    }

    private static Error Invalid(Subscription s, SubscriptionEvent e) =>
        Error.Conflict("subscriptions.invalid_transition", $"Cannot apply {e} to a {s.Status} subscription.");
}
