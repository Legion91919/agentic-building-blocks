using Agentic.Blocks.Core;
using Agentic.Blocks.Outbox;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentic.Blocks.Notifications;

/// <summary>Options: templates, channel defaults per category.</summary>
public sealed class NotificationOptions
{
    /// <summary>Templates by key.</summary>
    public IDictionary<string, NotificationTemplate> Templates { get; } = new Dictionary<string, NotificationTemplate>(StringComparer.Ordinal);

    /// <summary>Default channels per category (users can override).</summary>
    public IDictionary<string, NotificationChannel[]> CategoryDefaults { get; } = new Dictionary<string, NotificationChannel[]>(StringComparer.Ordinal);

    /// <summary>Channels for categories without defaults. Default in-app only.</summary>
    public IList<NotificationChannel> DefaultChannels { get; } = [NotificationChannel.InApp];

    /// <summary>Adds a template.</summary>
    public NotificationOptions AddTemplate(string key, string title, string body)
    {
        Templates[key] = new NotificationTemplate(key, title, body);
        return this;
    }

    /// <summary>Sets the default channels for a category.</summary>
    public NotificationOptions SetCategoryDefaults(string category, params NotificationChannel[] channels)
    {
        CategoryDefaults[category] = channels;
        return this;
    }

    /// <summary>The channels a user gets for a category, after their overrides.</summary>
    public IReadOnlyList<NotificationChannel> EffectiveChannels(string category, IReadOnlyDictionary<string, IReadOnlyDictionary<NotificationChannel, bool>> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var defaults = CategoryDefaults.TryGetValue(category, out var d) ? d : DefaultChannels.ToArray();
        var mine = overrides.GetValueOrDefault(category);
        return Enum.GetValues<NotificationChannel>()
            .Where(c => mine is not null && mine.TryGetValue(c, out var on) ? on : defaults.Contains(c))
            .ToList();
    }
}

/// <summary>Sends notifications to users.</summary>
public interface INotificationService
{
    /// <summary>
    /// Stages a notification in the current unit of work (outbox). It is delivered after the unit of work commits,
    /// on each channel the user has enabled for <paramref name="category"/>, rendered from <paramref name="templateKey"/>. Returns the notification id.
    /// </summary>
    Task<Result<Guid>> SendAsync(string userId, string category, string templateKey, IReadOnlyDictionary<string, string>? data = null, CancellationToken cancellationToken = default);
}

/// <summary>Outbox message: a notification was requested.</summary>
/// <param name="NotificationId">Id.</param>
/// <param name="UserId">Recipient user.</param>
/// <param name="Category">Category.</param>
/// <param name="Template">Template key.</param>
/// <param name="Data">Template values.</param>
public sealed record NotificationRequested(Guid NotificationId, string UserId, string Category, string Template, IReadOnlyDictionary<string, string> Data);

/// <summary>Outbox message: deliver one notification on one channel.</summary>
/// <param name="Notification">The request.</param>
/// <param name="Channel">The channel.</param>
public sealed record NotificationDelivery(NotificationRequested Notification, NotificationChannel Channel);

internal sealed class NotificationService(IOutbox outbox, IOptions<NotificationOptions> options) : INotificationService
{
    public Task<Result<Guid>> SendAsync(string userId, string category, string templateKey, IReadOnlyDictionary<string, string>? data = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(category))
        {
            return Task.FromResult(Result.Failure<Guid>(Error.Validation("notifications.invalid", "User id and category are required.")));
        }

        if (!options.Value.Templates.ContainsKey(templateKey ?? string.Empty))
        {
            return Task.FromResult(Result.Failure<Guid>(Error.Validation("notifications.unknown_template", "template", $"Template '{templateKey}' is not registered.")));
        }

        var id = Guid.NewGuid();
        outbox.Enqueue(new NotificationRequested(id, userId, category, templateKey!, data ?? new Dictionary<string, string>()));
        return Task.FromResult(Result.Success(id));
    }
}

/// <summary>Fans a request out into one delivery message per enabled channel, so channels retry independently.</summary>
internal sealed class NotificationRequestedHandler(IOutbox outbox, INotificationPreferenceStore preferences, IOptions<NotificationOptions> options)
    : IOutboxHandler<NotificationRequested>
{
    public async Task HandleAsync(NotificationRequested message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var overrides = await preferences.GetAsync(message.UserId, cancellationToken).ConfigureAwait(false);
        foreach (var channel in options.Value.EffectiveChannels(message.Category, overrides))
        {
            outbox.Enqueue(new NotificationDelivery(message, channel));
        }
    }
}

internal sealed partial class NotificationDeliveryHandler(
    IOptions<NotificationOptions> options,
    IInboxStore inbox,
    INotificationPusher pusher,
    INotificationRecipientResolver recipients,
    IEmailSender email,
    ISmsSender sms,
    TimeProvider time,
    ILogger<NotificationDeliveryHandler> logger) : IOutboxHandler<NotificationDelivery>
{
    public async Task HandleAsync(NotificationDelivery message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var n = message.Notification;
        if (!options.Value.Templates.TryGetValue(n.Template, out var template))
        {
            throw new InvalidOperationException($"Template '{n.Template}' is no longer registered.");
        }

        var (title, body) = template.Render(n.Data);
        switch (message.Channel)
        {
            case NotificationChannel.InApp:
                var item = new InboxItem(n.NotificationId, n.UserId, n.Category, title, body, time.GetUtcNow(), null);
                if (await inbox.TryAddAsync(item, cancellationToken).ConfigureAwait(false))
                {
                    var unread = await inbox.UnreadCountAsync(n.UserId, cancellationToken).ConfigureAwait(false);
                    await pusher.PushAsync(n.UserId, item, unread, cancellationToken).ConfigureAwait(false);
                }

                break;
            case NotificationChannel.Email:
                var to = (await recipients.ResolveAsync(n.UserId, cancellationToken).ConfigureAwait(false))?.Email;
                if (to is null) { LogNoContact(logger, "email", n.NotificationId); break; }
                await email.SendAsync(new EmailMessage(to, title, body), cancellationToken).ConfigureAwait(false);
                break;
            case NotificationChannel.Sms:
                var phone = (await recipients.ResolveAsync(n.UserId, cancellationToken).ConfigureAwait(false))?.Phone;
                if (phone is null) { LogNoContact(logger, "sms", n.NotificationId); break; }
                await sms.SendAsync(new SmsMessage(phone, body), cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped {Channel} for notification {NotificationId}: no contact details.")]
    private static partial void LogNoContact(ILogger logger, string channel, Guid notificationId);
}

/// <summary>Development email sender: logs a masked recipient and the size only, never the content.</summary>
public sealed partial class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    /// <inheritdoc />
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        Log(logger, PersonalDataMasker.MaskEmail(message.To), message.Body.Length);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[dev email] to {Recipient}, {Length} characters (content not logged)")]
    private static partial void Log(ILogger logger, string recipient, int length);
}

/// <summary>Development SMS sender: logs a masked number and the size only.</summary>
public sealed partial class LoggingSmsSender(ILogger<LoggingSmsSender> logger) : ISmsSender
{
    /// <inheritdoc />
    public Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        Log(logger, PersonalDataMasker.MaskPhone(message.To), message.Body.Length);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[dev sms] to {Recipient}, {Length} characters (content not logged)")]
    private static partial void Log(ILogger logger, string recipient, int length);
}

/// <summary>SignalR hub for live in-app notifications. Clients listen for <c>notification</c> and <c>unreadCount</c>.</summary>
[Microsoft.AspNetCore.Authorization.Authorize]
public sealed class NotificationsHub : Hub;

/// <summary>Pushes through <see cref="NotificationsHub"/> to every connection of the user (by NameIdentifier).</summary>
public sealed class SignalRNotificationPusher(IHubContext<NotificationsHub> hub) : INotificationPusher
{
    /// <inheritdoc />
    public async Task PushAsync(string userId, InboxItem item, int unreadCount, CancellationToken cancellationToken = default)
    {
        var client = hub.Clients.User(userId);
        await client.SendAsync("notification", item, cancellationToken).ConfigureAwait(false);
        await client.SendAsync("unreadCount", unreadCount, cancellationToken).ConfigureAwait(false);
    }
}
