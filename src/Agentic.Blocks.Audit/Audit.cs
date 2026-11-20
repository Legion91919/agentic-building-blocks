using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentic.Blocks.Core;

namespace Agentic.Blocks.Audit;

/// <summary>What happened, as told by the caller. Keep <see cref="Reason"/> free of personal data.</summary>
/// <param name="Actor">Who did it: a user id or <c>system:&lt;job&gt;</c>.</param>
/// <param name="Action">What was done, for example <c>subscription.cancelled</c>.</param>
/// <param name="EntityType">The kind of thing affected, for example <c>subscription</c>.</param>
/// <param name="EntityId">The id of the thing affected.</param>
/// <param name="Reason">Why, optional.</param>
public sealed record AuditRecord(string Actor, string Action, string EntityType, string EntityId, string? Reason = null);

/// <summary>A stored, immutable audit entry. <see cref="Hash"/> chains to <see cref="PreviousHash"/>.</summary>
public sealed record AuditEntry
{
    /// <summary>Gap-free position in the log, starting at 1.</summary>
    public required long Sequence { get; init; }

    /// <summary>Unique id.</summary>
    public required Guid Id { get; init; }

    /// <summary>When it was recorded (UTC).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Who did it.</summary>
    public required string Actor { get; init; }

    /// <summary>What was done.</summary>
    public required string Action { get; init; }

    /// <summary>Kind of entity.</summary>
    public required string EntityType { get; init; }

    /// <summary>Entity id.</summary>
    public required string EntityId { get; init; }

    /// <summary>Why, if given.</summary>
    public string? Reason { get; init; }

    /// <summary>Correlation id of the request that caused it.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Hash of the previous entry (empty for the first).</summary>
    public required string PreviousHash { get; init; }

    /// <summary>SHA-256 over this entry's fields and <see cref="PreviousHash"/>.</summary>
    public required string Hash { get; init; }

    /// <summary>Computes the hash this entry should have.</summary>
    public string ComputeHash()
    {
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            Sequence, Id, Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), Actor, Action, EntityType, EntityId, Reason, CorrelationId, PreviousHash,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>Filter for audit queries.</summary>
/// <param name="EntityType">Entity type, or null.</param>
/// <param name="EntityId">Entity id, or null.</param>
/// <param name="Actor">Actor, or null.</param>
public sealed record AuditQuery(string? EntityType = null, string? EntityId = null, string? Actor = null);

/// <summary>
/// Append-only persistence port. Implementations must reject an append whose <see cref="AuditEntry.Sequence"/> is not
/// exactly last + 1 (for example a unique index on Sequence), and must never offer update or delete.
/// </summary>
public interface IAuditStore
{
    /// <summary>The newest entry, or null when empty.</summary>
    Task<AuditEntry?> GetLastAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends when <paramref name="entry"/> follows the current last entry; returns false on a race.</summary>
    Task<bool> TryAppendAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Newest-first page of entries matching <paramref name="query"/>, after <paramref name="beforeSequence"/> when given.</summary>
    Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, long? beforeSequence, int limit, CancellationToken cancellationToken = default);

    /// <summary>All entries in sequence order (for integrity checks and exports).</summary>
    IAsyncEnumerable<AuditEntry> ReadAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>Thread-safe in-memory audit store. Lost on restart; for tests and demos.</summary>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly object _gate = new();
    private readonly List<AuditEntry> _entries = [];

    /// <inheritdoc />
    public Task<AuditEntry?> GetLastAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) { return Task.FromResult(_entries.Count == 0 ? null : _entries[^1]); }
    }

    /// <inheritdoc />
    public Task<bool> TryAppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (entry.Sequence != _entries.Count + 1) return Task.FromResult(false);
            _entries.Add(entry);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, long? beforeSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            IReadOnlyList<AuditEntry> page = Enumerable.Reverse(_entries)
                .Where(e => beforeSequence is null || e.Sequence < beforeSequence)
                .Where(e => query.EntityType is null || e.EntityType == query.EntityType)
                .Where(e => query.EntityId is null || e.EntityId == query.EntityId)
                .Where(e => query.Actor is null || e.Actor == query.Actor)
                .Take(limit)
                .ToList();
            return Task.FromResult(page);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AuditEntry> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<AuditEntry> copy;
        lock (_gate) { copy = [.. _entries]; }
        foreach (var e in copy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return e;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Test hook: replaces an entry to simulate tampering at the storage layer.</summary>
    internal void TamperForTest(long sequence, Func<AuditEntry, AuditEntry> change)
    {
        lock (_gate) { _entries[(int)sequence - 1] = change(_entries[(int)sequence - 1]); }
    }
}

/// <summary>Writes and reads the audit log. There is deliberately no update or delete.</summary>
public interface IAuditLog
{
    /// <summary>Validates and appends a record, stamping time, correlation id and hash.</summary>
    Task<Result<AuditEntry>> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default);

    /// <summary>Newest-first history of one entity.</summary>
    Task<Result<CursorPage<AuditEntry>>> GetByEntityAsync(string entityType, string entityId, PageRequest page, CancellationToken cancellationToken = default);

    /// <summary>Newest-first history of one actor.</summary>
    Task<Result<CursorPage<AuditEntry>>> GetByActorAsync(string actor, PageRequest page, CancellationToken cancellationToken = default);

    /// <summary>Re-computes the hash chain; fails with the first broken sequence number.</summary>
    Task<Result> VerifyIntegrityAsync(CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IAuditLog"/>.</summary>
public sealed class AuditLog(IAuditStore store, TimeProvider time, ICorrelationContext? correlation = null) : IAuditLog
{
    private const int MaxAppendRetries = 20;

    /// <inheritdoc />
    public async Task<Result<AuditEntry>> AppendAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var invalid = Validate(record);
        if (invalid is not null) return invalid;

        for (var attempt = 0; attempt < MaxAppendRetries; attempt++)
        {
            var last = await store.GetLastAsync(cancellationToken).ConfigureAwait(false);
            var draft = new AuditEntry
            {
                Sequence = (last?.Sequence ?? 0) + 1,
                Id = Guid.NewGuid(),
                Timestamp = time.GetUtcNow(),
                Actor = record.Actor.Trim(),
                Action = record.Action.Trim(),
                EntityType = record.EntityType.Trim(),
                EntityId = record.EntityId.Trim(),
                Reason = string.IsNullOrWhiteSpace(record.Reason) ? null : record.Reason.Trim(),
                CorrelationId = correlation?.CorrelationId,
                PreviousHash = last?.Hash ?? string.Empty,
                Hash = string.Empty,
            };
            var entry = draft with { Hash = draft.ComputeHash() };
            if (await store.TryAppendAsync(entry, cancellationToken).ConfigureAwait(false)) return entry;
        }

        return Error.Conflict("audit.append_contention", "The audit log is busy. Retry the operation.");
    }

    /// <inheritdoc />
    public Task<Result<CursorPage<AuditEntry>>> GetByEntityAsync(string entityType, string entityId, PageRequest page, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityType) || string.IsNullOrWhiteSpace(entityId))
        {
            return Task.FromResult(Result.Failure<CursorPage<AuditEntry>>(Error.Validation("audit.query_invalid", "Entity type and id are required.")));
        }

        return QueryAsync(new AuditQuery(EntityType: entityType, EntityId: entityId), page, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<CursorPage<AuditEntry>>> GetByActorAsync(string actor, PageRequest page, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Task.FromResult(Result.Failure<CursorPage<AuditEntry>>(Error.Validation("audit.query_invalid", "Actor is required.")));
        }

        return QueryAsync(new AuditQuery(Actor: actor), page, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
    {
        var previous = string.Empty;
        long expected = 1;
        await foreach (var entry in store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (entry.Sequence != expected || entry.PreviousHash != previous || entry.Hash != entry.ComputeHash())
            {
                return Error.Conflict("audit.integrity_broken", $"The audit chain is broken at sequence {expected}.")
                    .WithMetadata("sequence", expected);
            }

            previous = entry.Hash;
            expected++;
        }

        return Result.Success();
    }

    private async Task<Result<CursorPage<AuditEntry>>> QueryAsync(AuditQuery query, PageRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        long? before = null;
        if (page.Cursor is not null)
        {
            if (!CursorPagination.TryDecode(page.Cursor, out var key) || !long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
            {
                return Error.Validation("pagination.invalid_cursor", "cursor", "The cursor is malformed.");
            }

            before = seq;
        }

        var rows = await store.QueryAsync(query, before, page.Limit + 1, ct).ConfigureAwait(false);
        var items = rows.Take(page.Limit).ToList();
        var next = rows.Count > page.Limit ? CursorPagination.Encode(items[^1].Sequence.ToString(CultureInfo.InvariantCulture)) : null;
        return new CursorPage<AuditEntry>(items, next);
    }

    private static Error? Validate(AuditRecord r)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Check(errors, "actor", r.Actor, 200);
        Check(errors, "action", r.Action, 100);
        Check(errors, "entityType", r.EntityType, 100);
        Check(errors, "entityId", r.EntityId, 200);
        if (r.Reason is { Length: > 2000 }) errors["reason"] = ["Reason must be at most 2000 characters."];
        return errors.Count == 0 ? null : Error.Validation("audit.invalid_record", "The audit record is invalid.", errors);
    }

    private static void Check(Dictionary<string, string[]> errors, string field, string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) errors[field] = [$"{field} is required."];
        else if (value.Length > max) errors[field] = [$"{field} must be at most {max} characters."];
    }
}
