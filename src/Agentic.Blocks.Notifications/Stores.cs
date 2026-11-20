using Agentic.Blocks.Core;

namespace Agentic.Blocks.Notifications;

/// <summary>In-app inbox persistence port.</summary>
public interface IInboxStore
{
    /// <summary>Adds an item; returns false when the id already exists (redelivery).</summary>
    Task<bool> TryAddAsync(InboxItem item, CancellationToken cancellationToken = default);

    /// <summary>Newest-first page of a user's items.</summary>
    Task<Result<CursorPage<InboxItem>>> PageAsync(string userId, PageRequest page, CancellationToken cancellationToken = default);

    /// <summary>Unread count.</summary>
    Task<int> UnreadCountAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Marks one item read; returns false when it is not the user's item.</summary>
    Task<bool> MarkReadAsync(string userId, Guid id, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Marks all read; returns how many changed.</summary>
    Task<int> MarkAllReadAsync(string userId, DateTimeOffset at, CancellationToken cancellationToken = default);
}

/// <summary>Per-user channel overrides per category.</summary>
public interface INotificationPreferenceStore
{
    /// <summary>The user's overrides: category to channel to enabled.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<NotificationChannel, bool>>> GetAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Turns a channel on or off for a category.</summary>
    Task SetAsync(string userId, string category, NotificationChannel channel, bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>Thread-safe in-memory inbox.</summary>
public sealed class InMemoryInboxStore : IInboxStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, InboxItem> _items = [];

    /// <inheritdoc />
    public Task<bool> TryAddAsync(InboxItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate) { return Task.FromResult(_items.TryAdd(item.Id, item)); }
    }

    /// <inheritdoc />
    public Task<Result<CursorPage<InboxItem>>> PageAsync(string userId, PageRequest page, CancellationToken cancellationToken = default)
    {
        List<InboxItem> mine;
        lock (_gate) { mine = _items.Values.Where(i => i.UserId == userId).ToList(); }
        return Task.FromResult(CursorPagination.Page(mine, i => CursorPagination.TimeKey(i.CreatedAt, i.Id.ToString("N")), page, descending: true));
    }

    /// <inheritdoc />
    public Task<int> UnreadCountAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_gate) { return Task.FromResult(_items.Values.Count(i => i.UserId == userId && i.IsUnread)); }
    }

    /// <inheritdoc />
    public Task<bool> MarkReadAsync(string userId, Guid id, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var item) || item.UserId != userId) return Task.FromResult(false);
            if (item.IsUnread) _items[id] = item with { ReadAt = at };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<int> MarkAllReadAsync(string userId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var unread = _items.Values.Where(i => i.UserId == userId && i.IsUnread).ToList();
            foreach (var i in unread) _items[i.Id] = i with { ReadAt = at };
            return Task.FromResult(unread.Count);
        }
    }
}

/// <summary>Thread-safe in-memory preferences.</summary>
public sealed class InMemoryPreferenceStore : INotificationPreferenceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, Dictionary<NotificationChannel, bool>>> _prefs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<NotificationChannel, bool>>> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<string, IReadOnlyDictionary<NotificationChannel, bool>> copy = _prefs.TryGetValue(userId, out var p)
                ? p.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<NotificationChannel, bool>)new Dictionary<NotificationChannel, bool>(kv.Value), StringComparer.Ordinal)
                : new Dictionary<string, IReadOnlyDictionary<NotificationChannel, bool>>(StringComparer.Ordinal);
            return Task.FromResult(copy);
        }
    }

    /// <inheritdoc />
    public Task SetAsync(string userId, string category, NotificationChannel channel, bool enabled, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_prefs.TryGetValue(userId, out var byCategory)) _prefs[userId] = byCategory = new(StringComparer.Ordinal);
            if (!byCategory.TryGetValue(category, out var byChannel)) byCategory[category] = byChannel = [];
            byChannel[channel] = enabled;
        }

        return Task.CompletedTask;
    }
}

/// <summary>In-memory contact directory; register contacts with <see cref="Set"/>.</summary>
public sealed class InMemoryRecipientDirectory : INotificationRecipientResolver
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, NotificationRecipient> _contacts = new(StringComparer.Ordinal);

    /// <summary>Sets a user's contact details.</summary>
    public void Set(string userId, string? email, string? phone) => _contacts[userId] = new NotificationRecipient(email, phone);

    /// <inheritdoc />
    public Task<NotificationRecipient?> ResolveAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_contacts.GetValueOrDefault(userId));
}
