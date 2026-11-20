using System.Buffers.Text;
using System.Text;

namespace Agentic.Blocks.Core;

/// <summary>A request for one page: an opaque cursor from the previous page (or null) and a page size.</summary>
public sealed record PageRequest
{
    /// <summary>Default page size.</summary>
    public const int DefaultLimit = 20;

    /// <summary>Largest allowed page size.</summary>
    public const int MaxLimit = 100;

    /// <summary>Creates a request; the limit is clamped to 1..<see cref="MaxLimit"/>.</summary>
    public PageRequest(string? cursor = null, int? limit = null)
    {
        Cursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor;
        Limit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
    }

    /// <summary>The opaque cursor, or null for the first page.</summary>
    public string? Cursor { get; }

    /// <summary>The page size.</summary>
    public int Limit { get; }
}

/// <summary>One page of items and the cursor for the next page (null when this is the last page).</summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor)
{
    /// <summary>True when another page exists.</summary>
    public bool HasMore => NextCursor is not null;
}

/// <summary>
/// Keyset pagination helpers. Cursors are opaque, URL-safe strings that wrap the sort key of the last item,
/// so pages stay stable while new items are inserted (unlike offset paging).
/// </summary>
public static class CursorPagination
{
    /// <summary>Encodes a sort key as an opaque URL-safe cursor.</summary>
    public static string Encode(string sortKey)
    {
        ArgumentNullException.ThrowIfNull(sortKey);
        var bytes = Encoding.UTF8.GetBytes(sortKey);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Decodes a cursor; returns false for a malformed cursor.</summary>
    public static bool TryDecode(string? cursor, out string sortKey)
    {
        sortKey = string.Empty;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 1024) return false;
        var s = cursor.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        var buffer = new byte[s.Length];
        if (!Convert.TryFromBase64String(s, buffer, out var written)) return false;
        sortKey = Encoding.UTF8.GetString(buffer, 0, written);
        return true;
    }

    /// <summary>
    /// Pages an in-memory sequence by a unique, ordinal-sortable key (for example <c>{ticks:D19}|{id}</c>).
    /// Returns a validation error for a malformed cursor.
    /// </summary>
    public static Result<CursorPage<T>> Page<T>(IEnumerable<T> source, Func<T, string> sortKey, PageRequest request, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sortKey);
        ArgumentNullException.ThrowIfNull(request);

        string? after = null;
        if (request.Cursor is not null)
        {
            if (!TryDecode(request.Cursor, out var key))
            {
                return Error.Validation("pagination.invalid_cursor", "cursor", "The cursor is malformed.");
            }

            after = key;
        }

        var keyed = source.Select(item => (Key: sortKey(item), Item: item));
        var ordered = descending
            ? keyed.OrderByDescending(x => x.Key, StringComparer.Ordinal)
            : keyed.OrderBy(x => x.Key, StringComparer.Ordinal);
        IEnumerable<(string Key, T Item)> filtered = ordered;
        if (after is not null)
        {
            filtered = descending
                ? ordered.Where(x => string.CompareOrdinal(x.Key, after) < 0)
                : ordered.Where(x => string.CompareOrdinal(x.Key, after) > 0);
        }

        var window = filtered.Take(request.Limit + 1).ToList();
        var hasMore = window.Count > request.Limit;
        var items = window.Take(request.Limit).ToList();
        var next = hasMore ? Encode(items[^1].Key) : null;
        return new CursorPage<T>(items.Select(x => x.Item).ToList(), next);
    }

    /// <summary>A sort key that orders chronologically, then by id: <c>{utcTicks:D19}|{id}</c>.</summary>
    public static string TimeKey(DateTimeOffset timestamp, string id) =>
        string.Concat(timestamp.UtcTicks.ToString("D19", System.Globalization.CultureInfo.InvariantCulture), "|", id);
}
