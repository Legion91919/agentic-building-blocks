using System.Net;
using System.Text.Json;
using Agentic.Blocks.Core;
using Agentic.Blocks.Subscriptions;
using Agentic.Blocks.Testing;
using Agentic.Blocks.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agentic.Blocks.Subscriptions.Tests;

public sealed class RecordingSink : ISubscriptionEventSink
{
    public List<SubscriptionChanged> Changes { get; } = [];

    public Task OnChangedAsync(SubscriptionChanged change, CancellationToken cancellationToken = default)
    {
        Changes.Add(change);
        return Task.CompletedTask;
    }
}

public sealed class Billing
{
    public Billing()
    {
        Options.AddPlan("plus", "Plus", Money.Of(9.99m, "EUR"), BillingInterval.Monthly, 0, "hd", "offline")
            .AddPlan("family", "Family", Money.Of(14.99m, "EUR"), BillingInterval.Monthly, 7, "hd", "offline", "family");
        Fake = new FakePaymentProvider(Options.FakeProvider, Clock);
        Service = new SubscriptionService(Store, Fake, Sink, Clock, Microsoft.Extensions.Options.Options.Create(Options));
    }

    public ManualTimeProvider Clock { get; } = new();
    public SubscriptionOptions Options { get; } = new();
    public InMemorySubscriptionStore Store { get; } = new();
    public RecordingSink Sink { get; } = new();
    public FakePaymentProvider Fake { get; }
    public SubscriptionService Service { get; }

    public async Task<SubscriptionView> SubscribeAsync(string user, string plan)
    {
        var checkout = (await Service.StartCheckoutAsync(user, plan)).Value;
        var (body, headers) = Fake.CreateSignedEvent(PaymentEventType.CheckoutCompleted, checkoutSessionId: checkout.SessionId);
        Assert.True((await Service.HandleWebhookAsync(body, headers)).IsSuccess);
        return (await Service.GetCurrentAsync(user))!;
    }

    public Task<Result<WebhookOutcome>> SendAsync(PaymentEventType type, string subscriptionId, string? eventId = null)
    {
        var (body, headers) = Fake.CreateSignedEvent(type, subscriptionId: subscriptionId, eventId: eventId);
        return Service.HandleWebhookAsync(body, headers);
    }
}

public class SubscriptionServiceTests
{
    [Fact]
    public async Task Checkout_returns_hosted_url_and_webhook_activates()
    {
        var b = new Billing();
        var checkout = (await b.Service.StartCheckoutAsync("u-1", "plus")).Value;
        Assert.StartsWith("https://pay.example.com/checkout/cs_", checkout.Url.ToString());
        Assert.Null(await b.Service.GetCurrentAsync("u-1"));
        var view = await b.SubscribeAsync("u-2", "plus");
        Assert.Equal(SubscriptionStatus.Active, view.Subscription.Status);
        Assert.Contains("hd", view.Entitlements);
        Assert.Null(b.Sink.Changes[0].Before);
    }

    [Fact]
    public async Task Unknown_plan_is_not_found_and_double_subscribe_is_conflict()
    {
        var b = new Billing();
        Assert.Equal(ErrorType.NotFound, (await b.Service.StartCheckoutAsync("u-1", "gold")).Error.Type);
        await b.SubscribeAsync("u-1", "plus");
        Assert.Equal("subscriptions.already_subscribed", (await b.Service.StartCheckoutAsync("u-1", "family")).Error.Code);
    }

    [Fact]
    public async Task Duplicate_webhook_event_is_applied_once()
    {
        var b = new Billing();
        var sub = (await b.SubscribeAsync("u-1", "plus")).Subscription;
        b.Clock.Advance(TimeSpan.FromDays(25));
        var first = await b.SendAsync(PaymentEventType.PaymentSucceeded, sub.Id, "evt_renew_1");
        var periodEnd = (await b.Store.GetAsync(sub.Id))!.CurrentPeriodEnd;
        var second = await b.SendAsync(PaymentEventType.PaymentSucceeded, sub.Id, "evt_renew_1");
        Assert.False(first.Value.Duplicate);
        Assert.True(second.Value.Duplicate);
        Assert.Equal(periodEnd, (await b.Store.GetAsync(sub.Id))!.CurrentPeriodEnd);
        Assert.Equal(sub.CurrentPeriodEnd.AddMonths(1), periodEnd);
    }

    [Fact]
    public async Task Tampered_or_stale_webhook_is_rejected()
    {
        var b = new Billing();
        var (body, headers) = b.Fake.CreateSignedEvent(PaymentEventType.PaymentFailed, subscriptionId: "s");
        Assert.Equal("subscriptions.webhook_signature", (await b.Service.HandleWebhookAsync(body.Replace("PaymentFailed", "PaymentSucceeded", StringComparison.Ordinal), headers)).Error.Code);
        Assert.Equal("subscriptions.webhook_signature", (await b.Service.HandleWebhookAsync(body, new Dictionary<string, string>())).Error.Code);
        b.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("subscriptions.webhook_signature", (await b.Service.HandleWebhookAsync(body, headers)).Error.Code);
    }

    [Fact]
    public async Task Failed_processing_releases_event_id_for_provider_retry()
    {
        var b = new Billing();
        var (body, headers) = b.Fake.CreateSignedEvent(PaymentEventType.CheckoutCompleted, checkoutSessionId: "cs_unknown", eventId: "evt_x");
        Assert.Equal(ErrorType.NotFound, (await b.Service.HandleWebhookAsync(body, headers)).Error.Type);
        Assert.True(await b.Store.TryMarkEventProcessedAsync("fake", "evt_x"));
    }

    [Fact]
    public async Task Payment_failure_keeps_access_in_grace_then_expires()
    {
        var b = new Billing();
        var sub = (await b.SubscribeAsync("u-1", "plus")).Subscription;
        await b.SendAsync(PaymentEventType.PaymentFailed, sub.Id);
        Assert.Equal(SubscriptionStatus.PastDue, (await b.Service.GetCurrentAsync("u-1"))!.Subscription.Status);
        Assert.True(await b.Service.HasEntitlementAsync("u-1", "hd"));
        b.Clock.Advance(TimeSpan.FromDays(7));
        Assert.False(await b.Service.HasEntitlementAsync("u-1", "hd"));
        Assert.Equal(SubscriptionStatus.Expired, (await b.Store.GetAsync(sub.Id))!.Status);
    }

    [Fact]
    public async Task Unpaid_renewal_goes_past_due_at_period_end_via_clock()
    {
        var b = new Billing();
        var sub = (await b.SubscribeAsync("u-1", "plus")).Subscription;
        b.Clock.SetUtcNow(sub.CurrentPeriodEnd);
        Assert.Equal(1, await b.Service.RefreshAllAsync());
        Assert.Equal(SubscriptionStatus.PastDue, (await b.Store.GetAsync(sub.Id))!.Status);
        await b.SendAsync(PaymentEventType.PaymentSucceeded, sub.Id);
        Assert.Equal(SubscriptionStatus.Active, (await b.Store.GetAsync(sub.Id))!.Status);
    }

    [Fact]
    public async Task Trial_converts_on_payment()
    {
        var b = new Billing();
        var view = await b.SubscribeAsync("u-1", "family");
        Assert.Equal(SubscriptionStatus.Trialing, view.Subscription.Status);
        Assert.Contains("family", view.Entitlements);
        b.Clock.Advance(TimeSpan.FromDays(7));
        await b.SendAsync(PaymentEventType.PaymentSucceeded, view.Subscription.Id);
        Assert.Equal(SubscriptionStatus.Active, (await b.Store.GetAsync(view.Subscription.Id))!.Status);
    }

    [Fact]
    public async Task Cancel_at_period_end_keeps_access_until_then()
    {
        var b = new Billing();
        var sub = (await b.SubscribeAsync("u-1", "plus")).Subscription;
        Assert.True((await b.Service.CancelAsync("u-1", atPeriodEnd: true)).Value.CancelAtPeriodEnd);
        b.Clock.SetUtcNow(sub.CurrentPeriodEnd.AddSeconds(-1));
        Assert.True(await b.Service.HasEntitlementAsync("u-1", "hd"));
        b.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await b.Service.HasEntitlementAsync("u-1", "hd"));
        Assert.Equal(SubscriptionStatus.Cancelled, (await b.Store.GetAsync(sub.Id))!.Status);
    }

    [Fact]
    public async Task Sink_receives_every_status_change()
    {
        var b = new Billing();
        var sub = (await b.SubscribeAsync("u-1", "plus")).Subscription;
        await b.SendAsync(PaymentEventType.PaymentFailed, sub.Id);
        await b.Service.CancelAsync("u-1", atPeriodEnd: false);
        Assert.Equal([SubscriptionStatus.Active, SubscriptionStatus.PastDue, SubscriptionStatus.Cancelled], b.Sink.Changes.Select(c => c.After.Status).ToList());
    }
}

public class SubscriptionEndpointTests
{
    private static Task<KestrelTestHost> StartAsync() =>
        KestrelTestHost.StartAsync(
            b =>
            {
                TestAuthHandler.Register(b.Services);
                b.Services.AddBlocksWeb().AddBlocksSubscriptions(o => o.AddPlan("plus", "Plus", Money.Of(9.99m, "EUR"), BillingInterval.Monthly, 0, "hd"));
            },
            app =>
            {
                app.UseBlocksWeb();
                app.MapBlocksSubscriptions();
                app.MapGet("/media/hd", () => "hd stream").RequireEntitlement("hd");
            });

    private static Dictionary<string, string> As(string user) => new() { ["X-Test-User"] = user };

    [Fact]
    public async Task Entitlement_402_then_subscribe_then_200()
    {
        await using var host = await StartAsync();
        var denied = await host.SendAsync(HttpMethod.Get, "/media/hd", headers: As("u-1"));
        Assert.Equal(HttpStatusCode.PaymentRequired, denied.StatusCode);
        var problem = JsonDocument.Parse(await denied.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("subscriptions.entitlement_required", problem.GetProperty("code").GetString());
        Assert.Equal("/subscriptions/plans", problem.GetProperty("renewUrl").GetString());
        Assert.Equal("hd", problem.GetProperty("entitlement").GetString());

        var checkout = await host.SendAsync(HttpMethod.Post, "/subscriptions/checkout", new { planId = "plus" }, headers: As("u-1"));
        Assert.Equal(HttpStatusCode.Created, checkout.StatusCode);
        var sessionId = JsonDocument.Parse(await checkout.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, $"/subscriptions/dev/checkout/{sessionId}/complete", headers: As("u-2"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Post, $"/subscriptions/dev/checkout/{sessionId}/complete", headers: As("u-1"))).StatusCode);

        var allowed = await host.SendAsync(HttpMethod.Get, "/media/hd", headers: As("u-1"));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("hd stream", await allowed.Content.ReadAsStringAsync());
        var me = await (await host.SendAsync(HttpMethod.Get, "/subscriptions/me", headers: As("u-1"))).Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Active\"", me);
    }

    [Fact]
    public async Task Entitlement_without_sign_in_is_401()
    {
        await using var host = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/media/hd")).StatusCode);
    }

    [Fact]
    public async Task Webhook_endpoint_verifies_signature_and_dedupes()
    {
        await using var host = await StartAsync();
        var fake = host.App.Services.GetRequiredService<FakePaymentProvider>();
        var (body, headers) = fake.CreateSignedEvent(PaymentEventType.PaymentFailed, subscriptionId: "missing", eventId: "evt_1");
        using var bad = new HttpRequestMessage(HttpMethod.Post, "/subscriptions/webhooks/fake") { Content = new StringContent(body) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(bad)).StatusCode);

        var checkout = await host.SendAsync(HttpMethod.Post, "/subscriptions/checkout", new { planId = "plus" }, headers: As("u-9"));
        var sessionId = JsonDocument.Parse(await checkout.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetString();
        var (cbody, cheaders) = fake.CreateSignedEvent(PaymentEventType.CheckoutCompleted, checkoutSessionId: sessionId, eventId: "evt_2");
        async Task<string> PostAsync()
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/subscriptions/webhooks/fake") { Content = new StringContent(cbody) };
            foreach (var (k, v) in cheaders) req.Headers.TryAddWithoutValidation(k, v);
            var res = await host.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            return await res.Content.ReadAsStringAsync();
        }

        Assert.Contains("\"duplicate\":false", await PostAsync());
        Assert.Contains("\"duplicate\":true", await PostAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, "/subscriptions/webhooks/other")).StatusCode);
    }

    [Fact]
    public async Task Plans_are_public()
    {
        await using var host = await StartAsync();
        var plans = await host.Client.GetStringAsync("/subscriptions/plans");
        Assert.Contains("\"id\":\"plus\"", plans);
        Assert.Contains("\"currency\":\"EUR\"", plans);
    }
}
