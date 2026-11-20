namespace Agentic.Blocks.Auth;

/// <summary>A local user account. Immutable; stores return new copies.</summary>
public sealed record UserAccount
{
    /// <summary>Stable user id (the <c>NameIdentifier</c> claim).</summary>
    public required string Id { get; init; }

    /// <summary>Email as entered.</summary>
    public required string Email { get; init; }

    /// <summary>Upper-cased email used for look-ups.</summary>
    public required string NormalizedEmail { get; init; }

    /// <summary>Versioned password hash.</summary>
    public required string PasswordHash { get; init; }

    /// <summary>Roles (role claims).</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>Optional tenant (the <c>tenant_id</c> claim).</summary>
    public string? TenantId { get; init; }

    /// <summary>Consecutive failed logins since the last success or lockout.</summary>
    public int FailedLoginCount { get; init; }

    /// <summary>End of the current lockout, if any.</summary>
    public DateTimeOffset? LockoutEndsAt { get; init; }

    /// <summary>Changes on logout and password change; tokens carrying an old stamp stop working.</summary>
    public required string SecurityStamp { get; init; }

    /// <summary>When the account was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Optimistic-concurrency version, maintained by the store.</summary>
    public long Version { get; init; }
}

/// <summary>
/// User persistence port. Swap the in-memory adapter for a database, or for a read-only adapter over an external
/// directory; app code only ever sees claims.
/// </summary>
public interface IUserStore
{
    /// <summary>Finds by id.</summary>
    Task<UserAccount?> FindByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Finds by normalized (upper-case) email.</summary>
    Task<UserAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default);

    /// <summary>Creates a user; returns false when the email is taken.</summary>
    Task<bool> TryCreateAsync(UserAccount user, CancellationToken cancellationToken = default);

    /// <summary>Saves when <see cref="UserAccount.Version"/> still matches the stored version; returns false on a race.</summary>
    Task<bool> TryUpdateAsync(UserAccount user, CancellationToken cancellationToken = default);
}

/// <summary>Thread-safe in-memory user store. Lost on restart; for tests, demos and prototypes.</summary>
public sealed class InMemoryUserStore : IUserStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, UserAccount> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idByEmail = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<UserAccount?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate) { return Task.FromResult(_byId.GetValueOrDefault(id)); }
    }

    /// <inheritdoc />
    public Task<UserAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_idByEmail.TryGetValue(normalizedEmail, out var id) ? _byId[id] : null);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryCreateAsync(UserAccount user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            if (_idByEmail.ContainsKey(user.NormalizedEmail) || _byId.ContainsKey(user.Id)) return Task.FromResult(false);
            _byId[user.Id] = user with { Version = 1 };
            _idByEmail[user.NormalizedEmail] = user.Id;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryUpdateAsync(UserAccount user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            if (!_byId.TryGetValue(user.Id, out var current) || current.Version != user.Version) return Task.FromResult(false);
            if (current.NormalizedEmail != user.NormalizedEmail) return Task.FromResult(false);
            _byId[user.Id] = user with { Version = current.Version + 1 };
            return Task.FromResult(true);
        }
    }
}
