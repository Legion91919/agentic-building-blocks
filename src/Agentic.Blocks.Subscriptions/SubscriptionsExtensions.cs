using System.Security.Claims;
using Agentic.Blocks.Core;
using Agentic.Blocks.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentic.Blocks.Subscriptions;

/// <summary>Checkout request body.</summary>
/// <param name="PlanId">Plan id.</param>
public sealed record StartCheckoutRequest(string PlanId);

/// <summary>Cancel request body.</summary>
/// <param name="AtPeriodEnd">Cancel at period end instead of now.</param>
public sealed record CancelRequest(bool AtPeriodEnd);

/// <summary>Registration, endpoint and filter extensions for subscriptions.</summary>
public static class SubscriptionsExtensions
{
    /// <summary>Registers the service, in-memory store, fake payment provider (replace <see cref="IPaymentProvider"/>), and the refresher.</summary>
    public static IServiceCollection AddBlocksSubscriptions(this IServiceCollection services, Action<SubscriptionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = services.AddOptions<SubscriptionOptions>().Validate(o => o.GracePeriod >= TimeSpan.Zero, "Grace period must not be negative.");
        if (configure is not null) builder.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISubscriptionStore, InMemorySubscriptionStore>();
        services.TryAddSingleton(sp => new FakePaymentProvider(sp.GetRequiredService<IOptions<SubscriptionOptions>>().Value.FakeProvider, sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<FakePaymentProvider>());
        services.TryAddSingleton<ISubscriptionEventSink, NoopSubscriptionEventSink>();
        services.TryAddSingleton<SubscriptionService>();
        services.AddHostedService<SubscriptionRefresher>();
        return services;
    }

    /// <summary>
    /// Allows the request only when the signed-in user has <paramref name="entitlement"/>. Otherwise returns 402
    /// ProblemDetails with <c>code</c>, <c>entitlement</c> and <c>renewUrl</c> (401 when not signed in).
    /// </summary>
    public static TBuilder RequireEntitlement<TBuilder>(this TBuilder builder, string entitlement) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entitlement);
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var userId = http.User.GetUserId();
            if (userId is null) return Error.Unauthorized("auth.required", "Sign in to continue.").ToProblem();
            var service = http.RequestServices.GetRequiredService<SubscriptionService>();
            if (await service.HasEntitlementAsync(userId, entitlement, http.RequestAborted).ConfigureAwait(false)) return await next(context).ConfigureAwait(false);
            var options = http.RequestServices.GetRequiredService<IOptions<SubscriptionOptions>>().Value;
            return Error.PaymentRequired("subscriptions.entitlement_required", $"Your plan does not include '{entitlement}'. Subscribe or renew to continue.")
                .WithMetadata("entitlement", entitlement)
                .WithMetadata("renewUrl", options.RenewUrl)
                .ToProblem();
        });
        return builder;
    }

    /// <summary>
    /// Maps <c>GET plans</c>, <c>GET me</c>, <c>POST checkout</c>, <c>POST cancel</c>, <c>POST webhooks/{provider}</c>
    /// and, in Development with the fake provider, simulators under <c>dev/</c>.
    /// </summary>
    public static RouteGroupBuilder MapBlocksSubscriptions(this IEndpointRouteBuilder endpoints, string prefix = "/subscriptions")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.ServiceProvider.EnsureBlocksWeb("Agentic.Blocks.Subscriptions");
        var group = endpoints.MapGroup(prefix).WithTags("Subscriptions");

        group.MapGet("/plans", (SubscriptionService s) => Results.Ok(s.Plans.Select(p => new
        {
            p.Id,
            p.Name,
            price = p.Price.Amount,
            currency = p.Price.Currency,
            interval = p.Interval.ToString(),
            p.TrialDays,
            entitlements = p.Entitlements.Order(StringComparer.Ordinal),
        })));

        group.MapGet("/me", async (ClaimsPrincipal user, SubscriptionService s, CancellationToken ct) =>
        {
            var view = await s.GetCurrentAsync(user.GetUserId()!, ct).ConfigureAwait(false);
            return view is null
                ? Error.NotFound("subscriptions.none", "You have no subscription.").ToProblem()
                : Results.Ok(ToDto(view));
        }).RequireAuthorization();

        group.MapPost("/checkout", async (StartCheckoutRequest request, ClaimsPrincipal user, SubscriptionService s, CancellationToken ct) =>
            (await s.StartCheckoutAsync(user.GetUserId()!, request.PlanId, ct).ConfigureAwait(false))
                .ToHttpResult(c => Results.Created(c.Url, new { c.SessionId, url = c.Url, c.ExpiresAt })))
            .RequireAuthorization();

        group.MapPost("/cancel", async (CancelRequest request, ClaimsPrincipal user, SubscriptionService s, CancellationToken ct) =>
            (await s.CancelAsync(user.GetUserId()!, request.AtPeriodEnd, ct).ConfigureAwait(false)).ToHttpResult(sub => Results.Ok(new { sub.Id, status = sub.Status.ToString(), sub.CancelAtPeriodEnd })))
            .RequireAuthorization();

        group.MapPost("/webhooks/{provider}", async (string provider, HttpRequest request, IPaymentProvider payments, SubscriptionService s, CancellationToken ct) =>
        {
            if (!string.Equals(provider, payments.Name, StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            return (await s.HandleWebhookAsync(body, headers, ct).ConfigureAwait(false)).ToHttpResult();
        }).AllowAnonymous().RequireRateLimiting(BlocksRateLimitPolicies.Webhooks);

        var env = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>();
        if (env.IsDevelopment() && endpoints.ServiceProvider.GetRequiredService<IPaymentProvider>() is FakePaymentProvider)
        {
            var dev = group.MapGroup("/dev").RequireAuthorization().WithTags("Subscriptions (development)");
            dev.MapPost("/checkout/{sessionId}/complete", async (string sessionId, ClaimsPrincipal user, FakePaymentProvider fake, ISubscriptionStore store, SubscriptionService s, CancellationToken ct) =>
            {
                var checkout = await store.GetCheckoutAsync(sessionId, ct).ConfigureAwait(false);
                if (checkout is null || checkout.UserId != user.GetUserId()) return Error.NotFound("subscriptions.checkout_not_found", "Unknown checkout session.").ToProblem();
                var (body, headers) = fake.CreateSignedEvent(PaymentEventType.CheckoutCompleted, checkoutSessionId: sessionId);
                var result = await s.HandleWebhookAsync(body, headers, ct).ConfigureAwait(false);
                if (result.IsFailure) return result.Error.ToProblem();
                var view = await s.GetCurrentAsync(checkout.UserId, ct).ConfigureAwait(false);
                return Results.Ok(new { webhook = result.Value, subscription = view is null ? null : ToDto(view) });
            });
            dev.MapPost("/renewal/{outcome}", async (string outcome, ClaimsPrincipal user, FakePaymentProvider fake, SubscriptionService s, CancellationToken ct) =>
            {
                var view = await s.GetCurrentAsync(user.GetUserId()!, ct).ConfigureAwait(false);
                if (view is null) return Error.NotFound("subscriptions.none", "You have no subscription.").ToProblem();
                var type = outcome == "succeeded" ? PaymentEventType.PaymentSucceeded : PaymentEventType.PaymentFailed;
                var (body, headers) = fake.CreateSignedEvent(type, subscriptionId: view.Subscription.Id);
                return (await s.HandleWebhookAsync(body, headers, ct).ConfigureAwait(false)).ToHttpResult();
            });
        }

        return group;
    }

    private static object ToDto(SubscriptionView v) => new
    {
        v.Subscription.Id,
        planId = v.Plan.Id,
        status = v.Subscription.Status.ToString(),
        v.Subscription.CurrentPeriodEnd,
        v.Subscription.TrialEndsAt,
        v.Subscription.CancelAtPeriodEnd,
        entitlements = v.Entitlements.Order(StringComparer.Ordinal),
    };
}
