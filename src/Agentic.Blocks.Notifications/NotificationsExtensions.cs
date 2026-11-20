using System.Security.Claims;
using Agentic.Blocks.Core;
using Agentic.Blocks.Outbox;
using Agentic.Blocks.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentic.Blocks.Notifications;

/// <summary>Preference change request.</summary>
/// <param name="Category">Category.</param>
/// <param name="Channel">Channel.</param>
/// <param name="Enabled">On or off.</param>
public sealed record PreferenceUpdate(string Category, NotificationChannel Channel, bool Enabled);

/// <summary>Registration and endpoint extensions for notifications.</summary>
public static class NotificationsExtensions
{
    /// <summary>
    /// Registers the notification service, outbox handlers, in-memory inbox/preferences/contacts, development email
    /// and SMS senders that log masked recipients only, and SignalR. Replace any port with your own adapter.
    /// </summary>
    public static IServiceCollection AddBlocksNotifications(this IServiceCollection services, Action<NotificationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = services.AddOptions<NotificationOptions>();
        if (configure is not null) builder.Configure(configure);
        services.AddBlocksOutbox();
        services.AddSignalR();
        services.TryAddScoped<INotificationService, NotificationService>();
        services.TryAddSingleton<IInboxStore, InMemoryInboxStore>();
        services.TryAddSingleton<INotificationPreferenceStore, InMemoryPreferenceStore>();
        services.TryAddSingleton<InMemoryRecipientDirectory>();
        services.TryAddSingleton<INotificationRecipientResolver>(sp => sp.GetRequiredService<InMemoryRecipientDirectory>());
        services.TryAddSingleton<IEmailSender, LoggingEmailSender>();
        services.TryAddSingleton<ISmsSender, LoggingSmsSender>();
        services.TryAddSingleton<INotificationPusher, SignalRNotificationPusher>();
        services.AddOutboxHandler<NotificationRequested, NotificationRequestedHandler>("notifications.requested.v1");
        services.AddOutboxHandler<NotificationDelivery, NotificationDeliveryHandler>("notifications.delivery.v1");
        return services;
    }

    /// <summary>
    /// Maps, for the signed-in user: <c>GET inbox</c>, <c>GET inbox/unread-count</c>, <c>POST inbox/{id}/read</c>,
    /// <c>POST inbox/read-all</c>, <c>GET preferences</c>, <c>PUT preferences</c> and the SignalR hub at <c>hub</c>.
    /// </summary>
    public static RouteGroupBuilder MapBlocksNotifications(this IEndpointRouteBuilder endpoints, string prefix = "/notifications")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.ServiceProvider.EnsureBlocksWeb("Agentic.Blocks.Notifications");
        var group = endpoints.MapGroup(prefix).WithTags("Notifications").RequireAuthorization();

        group.MapGet("/inbox", async (string? cursor, int? limit, ClaimsPrincipal user, IInboxStore inbox, CancellationToken ct) =>
            (await inbox.PageAsync(user.GetUserId()!, new PageRequest(cursor, limit), ct).ConfigureAwait(false)).ToHttpResult());
        group.MapGet("/inbox/unread-count", async (ClaimsPrincipal user, IInboxStore inbox, CancellationToken ct) =>
            Results.Ok(new { unread = await inbox.UnreadCountAsync(user.GetUserId()!, ct).ConfigureAwait(false) }));
        group.MapPost("/inbox/{id:guid}/read", async (Guid id, ClaimsPrincipal user, IInboxStore inbox, TimeProvider time, CancellationToken ct) =>
            await inbox.MarkReadAsync(user.GetUserId()!, id, time.GetUtcNow(), ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Error.NotFound("notifications.not_found", "No such notification.").ToProblem());
        group.MapPost("/inbox/read-all", async (ClaimsPrincipal user, IInboxStore inbox, TimeProvider time, CancellationToken ct) =>
            Results.Ok(new { marked = await inbox.MarkAllReadAsync(user.GetUserId()!, time.GetUtcNow(), ct).ConfigureAwait(false) }));
        group.MapGet("/preferences", async (ClaimsPrincipal user, INotificationPreferenceStore prefs, CancellationToken ct) =>
            Results.Ok(await prefs.GetAsync(user.GetUserId()!, ct).ConfigureAwait(false)));
        group.MapPut("/preferences", async (PreferenceUpdate update, ClaimsPrincipal user, INotificationPreferenceStore prefs, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(update.Category) || update.Category.Length > 64 || !Enum.IsDefined(update.Channel))
            {
                return Error.Validation("notifications.invalid_preference", "category", "Give a category (max 64 chars) and a valid channel.").ToProblem();
            }

            await prefs.SetAsync(user.GetUserId()!, update.Category, update.Channel, update.Enabled, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
        group.MapHub<NotificationsHub>("/hub");
        return group;
    }
}
