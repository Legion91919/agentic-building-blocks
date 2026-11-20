using System.Security.Claims;
using Agentic.Blocks.Audit;
using Agentic.Blocks.Auth;
using Agentic.Blocks.Core;
using Agentic.Blocks.Idempotency;
using Agentic.Blocks.Notifications;
using Agentic.Blocks.Outbox;
using Agentic.Blocks.Subscriptions;
using Agentic.Blocks.Web;
using Blocks.Sample.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpContextAccessor();
builder.Services
    .AddBlocksWeb()
    .AddBlocksIdempotency()
    .AddBlocksOutbox()
    .AddBlocksAudit()
    .AddBlocksAuth(o =>
    {
        o.QueryStringTokenPaths.Add("/notifications/hub");
        var demo = new DemoUser { Email = "demo@example.com", TenantId = "demo-tenant" };
        demo.Roles.Add("user");
        demo.Roles.Add("admin");
        o.DemoUsers.Add(demo);
    })
    .AddBlocksNotifications(o => o
        .AddTemplate("welcome", "Welcome to the demo", "Hi {{name}}, notifications are working.")
        .AddTemplate("subscription-changed", "Your subscription is {{status}}", "Plan {{plan}} is now {{status}}.")
        .AddTemplate("order-placed", "Order received", "We received order {{orderId}} for {{total}}.")
        .SetCategoryDefaults("billing", NotificationChannel.InApp, NotificationChannel.Email))
    .AddBlocksSubscriptions(o => o
        .AddPlan("basic", "Basic", Money.Of(4.99m, "EUR"), BillingInterval.Monthly, 0, "sd")
        .AddPlan("plus", "Plus", Money.Of(9.99m, "EUR"), BillingInterval.Monthly, 0, "sd", "hd", "offline")
        .AddPlan("family", "Family", Money.Of(14.99m, "EUR"), BillingInterval.Monthly, 7, "sd", "hd", "offline", "family"));

// Adapters that connect blocks without the blocks knowing each other.
builder.Services.AddSingleton<INotificationRecipientResolver, UserStoreRecipientResolver>();
builder.Services.AddSingleton<ISubscriptionEventSink, NotifyAndAuditOnSubscriptionChange>();

var app = builder.Build();

app.UseBlocksWeb();
app.UseBlocksOutbox();

app.MapBlocksAuth("/auth");
app.MapBlocksNotifications("/notifications");
app.MapBlocksSubscriptions("/subscriptions");
app.MapBlocksAudit("/audit");

app.MapGet("/", () => Results.Ok(new { name = "Agentic Building Blocks sample", docs = "See README.md" }));

var demo = app.MapGroup("/demo").RequireAuthorization().RequireRateLimiting(BlocksRateLimitPolicies.Api);

demo.MapPost("/orders", async (PlaceOrder request, ClaimsPrincipal user, IAuditLog audit, INotificationService notifications) =>
{
    var total = Money.TryOf(request.UnitPrice, request.Currency).Bind(price =>
        request.Quantity is < 1 or > 100
            ? Result.Failure<Money>(Error.Validation("order.quantity", "quantity", "Quantity must be 1 to 100."))
            : Result.Success(price * request.Quantity));
    if (total.IsFailure) return total.Error.ToProblem();

    var orderId = "ord_" + Guid.NewGuid().ToString("N")[..12];
    var userId = user.GetUserId()!;
    await audit.AppendAsync(new AuditRecord(userId, "order.placed", "order", orderId));
    await notifications.SendAsync(userId, "orders", "order-placed", new Dictionary<string, string> { ["orderId"] = orderId, ["total"] = total.Value.ToString() });
    return Results.Created($"/demo/orders/{orderId}", new { orderId, total = total.Value.ToString() });
}).RequireIdempotencyKey();

demo.MapPost("/notify-me", async (ClaimsPrincipal user, INotificationService notifications) =>
    (await notifications.SendAsync(user.GetUserId()!, "general", "welcome", new Dictionary<string, string> { ["name"] = user.Identity?.Name ?? "there" }))
        .ToHttpResult(id => Results.Accepted(value: new { notificationId = id })));

app.MapGet("/media/hd", (ClaimsPrincipal user) => Results.Ok(new { stream = "hd-demo-stream", quality = "1080p" }))
    .RequireEntitlement("hd");

app.Run();

namespace Blocks.Sample.Api
{
    /// <summary>Order request.</summary>
    public sealed record PlaceOrder(string Sku, int Quantity, decimal UnitPrice, string Currency);

    /// <summary>Resolves notification contacts from the Auth user store (email only in this demo).</summary>
    internal sealed class UserStoreRecipientResolver(IUserStore users) : INotificationRecipientResolver
    {
        public async Task<NotificationRecipient?> ResolveAsync(string userId, CancellationToken cancellationToken = default)
        {
            var user = await users.FindByIdAsync(userId, cancellationToken);
            return user is null ? null : new NotificationRecipient(user.Email, null);
        }
    }

    /// <summary>On every subscription change: stage a billing notification and write an audit entry.</summary>
    internal sealed class NotifyAndAuditOnSubscriptionChange(IHttpContextAccessor http, IServiceScopeFactory scopes, IAuditLog audit) : ISubscriptionEventSink
    {
        public async Task OnChangedAsync(SubscriptionChanged change, CancellationToken cancellationToken = default)
        {
            var s = change.After;
            await audit.AppendAsync(new AuditRecord(change.Cause.StartsWith("provider", StringComparison.Ordinal) ? "system:payments" : s.UserId,
                $"subscription.{s.Status.ToString().ToLowerInvariant()}", "subscription", s.Id, change.Cause), cancellationToken);

            // Inside a request the notification joins the request's unit of work; in background jobs we own the commit.
            var requestServices = http.HttpContext?.RequestServices;
            await using var scope = requestServices is null ? scopes.CreateAsyncScope() : (AsyncServiceScope?)null;
            var services = requestServices ?? scope!.Value.ServiceProvider;
            await services.GetRequiredService<INotificationService>().SendAsync(s.UserId, "billing", "subscription-changed",
                new Dictionary<string, string> { ["plan"] = s.PlanId, ["status"] = s.Status.ToString() }, cancellationToken);
            if (requestServices is null) await services.GetRequiredService<IOutbox>().CommitAsync(cancellationToken);
        }
    }
}
