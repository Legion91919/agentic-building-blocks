using System.Text.RegularExpressions;

namespace Agentic.Blocks.Notifications;

/// <summary>Delivery channels.</summary>
public enum NotificationChannel
{
    /// <summary>Stored inbox plus live push.</summary>
    InApp,
    /// <summary>Email through <see cref="IEmailSender"/>.</summary>
    Email,
    /// <summary>SMS through <see cref="ISmsSender"/>.</summary>
    Sms,
}

/// <summary>A template with <c>{{name}}</c> placeholders.</summary>
/// <param name="Key">Template key passed to <see cref="INotificationService.SendAsync"/>.</param>
/// <param name="Title">Title (email subject).</param>
/// <param name="Body">Body.</param>
public sealed partial record NotificationTemplate(string Key, string Title, string Body)
{
    /// <summary>Renders title and body; unknown placeholders become empty.</summary>
    public (string Title, string Body) Render(IReadOnlyDictionary<string, string> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        string Fill(string text) => Placeholder().Replace(text, m => data.TryGetValue(m.Groups[1].Value, out var v) ? v : string.Empty);
        return (Fill(Title), Fill(Body));
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.-]+)\s*\}\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Placeholder();
}

/// <summary>A stored in-app notification.</summary>
/// <param name="Id">Notification id.</param>
/// <param name="UserId">Owner.</param>
/// <param name="Category">Category, for example <c>billing</c>.</param>
/// <param name="Title">Rendered title.</param>
/// <param name="Body">Rendered body.</param>
/// <param name="CreatedAt">When it arrived.</param>
/// <param name="ReadAt">When it was read, or null.</param>
public sealed record InboxItem(Guid Id, string UserId, string Category, string Title, string Body, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt)
{
    /// <summary>True when unread.</summary>
    public bool IsUnread => ReadAt is null;
}

/// <summary>Contact details for a user. Personal data: never log it unmasked.</summary>
/// <param name="Email">Email, or null.</param>
/// <param name="Phone">Phone in E.164, or null.</param>
public sealed record NotificationRecipient(string? Email, string? Phone);

/// <summary>An email to send.</summary>
/// <param name="To">Recipient.</param>
/// <param name="Subject">Subject.</param>
/// <param name="Body">Plain-text body.</param>
public sealed record EmailMessage(string To, string Subject, string Body);

/// <summary>An SMS to send.</summary>
/// <param name="To">Recipient (E.164).</param>
/// <param name="Body">Text.</param>
public sealed record SmsMessage(string To, string Body);

/// <summary>Email port. Implement with your provider; throw to trigger an outbox retry.</summary>
public interface IEmailSender
{
    /// <summary>Sends an email.</summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>SMS port. Implement with your provider; throw to trigger an outbox retry.</summary>
public interface ISmsSender
{
    /// <summary>Sends an SMS.</summary>
    Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Looks up contact details for a user id (for example from your user store or CRM).</summary>
public interface INotificationRecipientResolver
{
    /// <summary>Returns contact details, or null when unknown.</summary>
    Task<NotificationRecipient?> ResolveAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>Pushes in-app notifications to connected clients.</summary>
public interface INotificationPusher
{
    /// <summary>Pushes a new item and the new unread count to the user's connections.</summary>
    Task PushAsync(string userId, InboxItem item, int unreadCount, CancellationToken cancellationToken = default);
}

/// <summary>Masks personal data for logs.</summary>
public static class PersonalDataMasker
{
    /// <summary><c>ada@example.com</c> becomes <c>a***@example.com</c>.</summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrEmpty(email)) return "(none)";
        var at = email.LastIndexOf('@');
        if (at <= 0) return "***";
        return string.Concat(email.AsSpan(0, 1), "***", email.AsSpan(at));
    }

    /// <summary>Keeps only the last four digits: <c>+15550101234</c> becomes <c>*******1234</c>.</summary>
    public static string MaskPhone(string? phone)
    {
        if (string.IsNullOrEmpty(phone)) return "(none)";
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length <= 4) return "****";
        return new string('*', digits.Length - 4) + digits[^4..];
    }
}
