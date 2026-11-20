using Agentic.Blocks.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentic.Blocks.Subscriptions;

/// <summary>Options for subscriptions.</summary>
public sealed class SubscriptionOptions
{
    /// <summary>Available plans.</summary>
    public IList<Plan> Plans { get; } = [];

    /// <summary>How long a PastDue subscription waits for payment before it expires. Default 7 days.</summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Keep entitlements while PastDue (in grace). Default true.</summary>
    public bool EntitlementsDuringGrace { get; set; } = true;

    /// <summary>Link returned in 402 responses. Default <c>/subscriptions/plans</c>.</summary>
    public string RenewUrl { get; set; } = "/subscriptions/plans";

    /// <summary>Where the provider returns the user after paying.</summary>
    public string CheckoutSuccessUrl { get; set; } = "/billing/success";

    /// <summary>Where the provider returns the user after cancelling.</summary>
    public string CheckoutCancelUrl { get; set; } = "/billing/cancelled";

    /// <summary>How often the background refresher applies time-driven transitions. Default 1 minute.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Settings for the fake provider (used when no other <see cref="IPaymentProvider"/> is registered).</summary>
    public FakePaymentOptions FakeProvider { get; } = new();

    /// <summary>Adds a plan.</summary>
    public SubscriptionOptions AddPlan(string id, string name, Money price, BillingInterval interval, int trialDays, params string[] entitlements)
    {
        Plans.Add(new Plan(id, name, price, interval, trialDays, new HashSet<string>(entitlements, StringComparer.Ordinal)));
        return this;
    }
}

/// <summary>A status change, for notifications, audit or analytics.</summary>
/// <param name="Before">State before (null when created).</param>
/// <param name="After">State after.</param>
/// <param name="Cause">What caused it.</param>
public sealed record SubscriptionChanged(Subscription? Before, Subscription After, string Cause);

/// <summary>Receives subscription changes. Default is a no-op; implement it to send notifications or write audit entries.</summary>
public interface ISubscriptionEventSink
{
    /// <summary>Called after a change is saved.</summary>
    Task OnChangedAsync(SubscriptionChanged change, CancellationToken cancellationToken = default);
}

internal sealed class NoopSubscriptionEventSink : ISubscriptionEventSink
{
    public Task OnChangedAsync(SubscriptionChanged change, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>The current subscription with its plan and live entitlements.</summary>
/// <param name="Subscription">Subscription.</param>
/// <param name="Plan">Plan.</param>
/// <param name="Entitlements">Entitlements granted right now (empty when access is not granted).</param>
public sealed record SubscriptionView(Subscription Subscription, Plan Plan, IReadOnlyCollection<string> Entitlements);

/// <summary>Outcome of webhook processing.</summary>
/// <param name="EventId">Provider event id.</param>
/// <param name="Duplicate">True when the event had already been processed.</param>
public sealed record WebhookOutcome(string EventId, bool Duplicate);

/// <summary>Subscription use cases.</summary>
public sealed class SubscriptionService(ISubscriptionStore store, IPaymentProvider provider, ISubscriptionEventSink sink, TimeProvider time, IOptions<SubscriptionOptions> options)
{
    private SubscriptionOptions Options => options.Value;

    /// <summary>All plans.</summary>
    public IReadOnlyList<Plan> Plans => Options.Plans.ToList();

    /// <summary>Finds a plan.</summary>
    public Plan? FindPlan(string planId) => Options.Plans.FirstOrDefault(p => p.Id == planId);

    /// <summary>The user's current subscription (time-driven transitions applied first), or null.</summary>
    public async Task<SubscriptionView?> GetCurrentAsync(string userId, CancellationToken cancellationToken = default)
    {
        var sub = await store.FindCurrentForUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (sub is null) return null;
        sub = await RefreshAsync(sub, cancellationToken).ConfigureAwait(false);
        var plan = FindPlan(sub.PlanId);
        if (plan is null || sub.IsTerminal) return null;
        var grants = SubscriptionStateMachine.GrantsAccess(sub, Options.EntitlementsDuringGrace);
        return new SubscriptionView(sub, plan, grants ? plan.Entitlements.ToList() : []);
    }

    /// <summary>True when the user currently has <paramref name="entitlement"/>.</summary>
    public async Task<bool> HasEntitlementAsync(string userId, string entitlement, CancellationToken cancellationToken = default)
    {
        var view = await GetCurrentAsync(userId, cancellationToken).ConfigureAwait(false);
        return view?.Entitlements.Contains(entitlement) == true;
    }

    /// <summary>Starts a hosted checkout for a plan.</summary>
    public async Task<Result<HostedCheckout>> StartCheckoutAsync(string userId, string planId, CancellationToken cancellationToken = default)
    {
        var plan = FindPlan(planId);
        if (plan is null) return Error.NotFound("subscriptions.plan_not_found", $"Plan '{planId}' does not exist.");
        var current = await GetCurrentAsync(userId, cancellationToken).ConfigureAwait(false);
        if (current is not null && current.Subscription.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing)
        {
            return Error.Conflict("subscriptions.already_subscribed", "You already have an active subscription.");
        }

        var checkout = await provider.CreateCheckoutSessionAsync(new CheckoutRequest(userId, plan, Options.CheckoutSuccessUrl, Options.CheckoutCancelUrl), cancellationToken).ConfigureAwait(false);
        await store.SaveCheckoutAsync(new PendingCheckout(checkout.SessionId, userId, plan.Id, checkout.ExpiresAt), cancellationToken).ConfigureAwait(false);
        return checkout;
    }

    /// <summary>Cancels now or at period end.</summary>
    public async Task<Result<Subscription>> CancelAsync(string userId, bool atPeriodEnd, CancellationToken cancellationToken = default)
    {
        var sub = await store.FindCurrentForUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (sub is null) return Error.NotFound("subscriptions.none", "You have no subscription.");
        return await ApplyAsync(sub.Id, atPeriodEnd ? SubscriptionEvent.CancelAtPeriodEnd : SubscriptionEvent.CancelNow, "user", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies and applies a provider webhook exactly once per provider event id. Duplicates succeed without side effects;
    /// failures release the event id so the provider's retry is processed.
    /// </summary>
    public async Task<Result<WebhookOutcome>> HandleWebhookAsync(string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        var parsed = provider.ParseWebhook(body, headers);
        if (parsed.IsFailure) return parsed.Error;
        var evt = parsed.Value;
        if (!await store.TryMarkEventProcessedAsync(provider.Name, evt.EventId, cancellationToken).ConfigureAwait(false))
        {
            return new WebhookOutcome(evt.EventId, Duplicate: true);
        }

        Result applied;
        try
        {
            applied = await ApplyEventAsync(evt, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await store.UnmarkEventAsync(provider.Name, evt.EventId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (applied.IsFailure)
        {
            await store.UnmarkEventAsync(provider.Name, evt.EventId, CancellationToken.None).ConfigureAwait(false);
            return applied.Error;
        }

        return new WebhookOutcome(evt.EventId, Duplicate: false);
    }

    /// <summary>Applies time-driven transitions to every open subscription. Returns how many changed.</summary>
    public async Task<int> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        var changed = 0;
        foreach (var sub in await store.ListOpenAsync(cancellationToken).ConfigureAwait(false))
        {
            var after = await RefreshAsync(sub, cancellationToken).ConfigureAwait(false);
            if (after.Status != sub.Status) changed++;
        }

        return changed;
    }

    /// <summary>Applies an event to a subscription by id (used by webhooks and development simulators).</summary>
    public async Task<Result<Subscription>> ApplyAsync(string subscriptionId, SubscriptionEvent e, string cause, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var sub = await store.GetAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
            if (sub is null) return Error.NotFound("subscriptions.not_found", "The subscription does not exist.");
            var plan = FindPlan(sub.PlanId) ?? throw new InvalidOperationException($"Plan '{sub.PlanId}' is no longer configured.");
            var now = time.GetUtcNow();
            var current = SubscriptionStateMachine.Advance(sub, now, plan, Options.GracePeriod);
            var next = SubscriptionStateMachine.Apply(current, e, now, plan, Options.GracePeriod);
            if (next.IsFailure) return next;
            if (next.Value == sub) return sub;
            if (await store.TryUpdateAsync(next.Value, cancellationToken).ConfigureAwait(false))
            {
                await sink.OnChangedAsync(new SubscriptionChanged(sub, next.Value, cause), cancellationToken).ConfigureAwait(false);
                return (await store.GetAsync(subscriptionId, cancellationToken).ConfigureAwait(false))!;
            }
        }

        return Error.Conflict("subscriptions.busy", "The subscription is busy. Try again.");
    }

    private async Task<Result> ApplyEventAsync(PaymentEvent evt, CancellationToken ct)
    {
        switch (evt.Type)
        {
            case PaymentEventType.CheckoutCompleted:
                var checkout = evt.CheckoutSessionId is null ? null : await store.GetCheckoutAsync(evt.CheckoutSessionId, ct).ConfigureAwait(false);
                if (checkout is null) return Error.NotFound("subscriptions.checkout_not_found", "Unknown checkout session.");
                var plan = FindPlan(checkout.PlanId);
                if (plan is null) return Error.NotFound("subscriptions.plan_not_found", "The plan no longer exists.");
                var existing = await store.FindCurrentForUserAsync(checkout.UserId, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    var replaced = await ApplyAsync(existing.Id, SubscriptionEvent.CancelNow, "replaced", ct).ConfigureAwait(false);
                    if (replaced.IsFailure && replaced.Error.Type != ErrorType.Conflict) return replaced.Error;
                }

                var created = SubscriptionStateMachine.Start("sub_" + Guid.NewGuid().ToString("N"), checkout.UserId, plan, time.GetUtcNow());
                await store.CreateAsync(created, ct).ConfigureAwait(false);
                await sink.OnChangedAsync(new SubscriptionChanged(null, created, "checkout"), ct).ConfigureAwait(false);
                return Result.Success();
            case PaymentEventType.PaymentSucceeded:
            case PaymentEventType.PaymentFailed:
            case PaymentEventType.SubscriptionCancelled:
                if (evt.SubscriptionId is null) return Error.Validation("subscriptions.webhook_invalid", "The event has no subscription id.");
                var e = evt.Type switch
                {
                    PaymentEventType.PaymentSucceeded => SubscriptionEvent.PaymentSucceeded,
                    PaymentEventType.PaymentFailed => SubscriptionEvent.PaymentFailed,
                    _ => SubscriptionEvent.CancelNow,
                };
                var result = await ApplyAsync(evt.SubscriptionId, e, "provider:" + evt.Type, ct).ConfigureAwait(false);
                return result.IsSuccess || result.Error.Type == ErrorType.Conflict ? Result.Success() : result.Error;
            default:
                return Result.Success();
        }
    }

    private async Task<Subscription> RefreshAsync(Subscription sub, CancellationToken ct)
    {
        var plan = FindPlan(sub.PlanId);
        if (plan is null) return sub;
        var advanced = SubscriptionStateMachine.Advance(sub, time.GetUtcNow(), plan, Options.GracePeriod);
        if (advanced == sub) return sub;
        var result = await ApplyAsync(sub.Id, SubscriptionEvent.TimePassed, "time", ct).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : advanced;
    }
}

/// <summary>Applies time-driven transitions in the background. Reads also refresh lazily, so correctness never depends on this job.</summary>
internal sealed partial class SubscriptionRefresher(SubscriptionService service, IOptions<SubscriptionOptions> options, TimeProvider time, ILogger<SubscriptionRefresher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await service.RefreshAllAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(options.Value.RefreshInterval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Subscription refresh failed; it will retry.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
