using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using Agentic.Blocks.Core;
using Agentic.Blocks.Web;
using Microsoft.Extensions.Options;

namespace Agentic.Blocks.Auth;

/// <summary>A demo account seeded in Development only.</summary>
public sealed class DemoUser
{
    /// <summary>Email (use a reserved domain such as example.com).</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Password; when empty a random one is generated and written to the Development console once.</summary>
    public string? Password { get; set; }

    /// <summary>Roles.</summary>
    public IList<string> Roles { get; } = [];

    /// <summary>Optional tenant.</summary>
    public string? TenantId { get; set; }
}

/// <summary>Options for the Auth block.</summary>
public sealed class AuthOptions
{
    /// <summary>Failed logins before lockout. Default 5.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>Lockout length. Default 15 minutes.</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Access-token lifetime. Default 15 minutes.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Refresh-token lifetime. Default 14 days.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Minimum password length. Default 12.</summary>
    public int MinPasswordLength { get; set; } = 12;

    /// <summary>Maximum password length (bounds hashing cost). Default 128.</summary>
    public int MaxPasswordLength { get; set; } = 128;

    /// <summary>Roles given to self-registered users. Default <c>user</c>.</summary>
    public IList<string> DefaultRoles { get; } = ["user"];

    /// <summary>Check the security stamp on every request so logout revokes access tokens at once. Default true.</summary>
    public bool ValidateSecurityStampOnEachRequest { get; set; } = true;

    /// <summary>Paths where the access token may come from the <c>access_token</c> query value (SignalR WebSockets).</summary>
    public IList<string> QueryStringTokenPaths { get; } = [];

    /// <summary>Rate-limit policy for register/login/refresh; null disables it. Default <see cref="BlocksRateLimitPolicies.Auth"/>.</summary>
    public string? RateLimitPolicy { get; set; } = BlocksRateLimitPolicies.Auth;

    /// <summary>Accounts to create at start-up in Development only.</summary>
    public IList<DemoUser> DemoUsers { get; } = [];

    /// <summary>PBKDF2 parameters.</summary>
    public PasswordHashingOptions Hashing { get; } = new();
}

/// <summary>Claim types and names used by the Auth block.</summary>
public static class AuthClaims
{
    /// <summary>Authentication type of identities issued by this block.</summary>
    public const string IdentityType = "Agentic.Blocks.Auth";

    /// <summary>Security-stamp claim.</summary>
    public const string SecurityStamp = "agentic.stamp";
}

/// <summary>Registration, login, refresh and logout rules, independent of HTTP.</summary>
public sealed class AuthService
{
    private readonly IUserStore _users;
    private readonly IPasswordHasher _hasher;
    private readonly TimeProvider _time;
    private readonly AuthOptions _options;
    private readonly Lazy<string> _dummyHash;

    /// <summary>Creates the service.</summary>
    public AuthService(IUserStore users, IPasswordHasher hasher, TimeProvider time, IOptions<AuthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _users = users;
        _hasher = hasher;
        _time = time;
        _options = options.Value;
        _dummyHash = new Lazy<string>(() => _hasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));
    }

    /// <summary>Upper-cases and trims an email for look-ups.</summary>
    public static string NormalizeEmail(string email) => (email ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Creates an account. Fails with <c>auth.email_taken</c> (409) or validation errors (400).</summary>
    public async Task<Result<UserAccount>> RegisterAsync(string email, string password, IEnumerable<string>? roles = null, string? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        email = (email ?? string.Empty).Trim();
        if (email.Length > 254 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
        {
            errors["email"] = ["Enter a valid email address."];
        }

        password ??= string.Empty;
        if (password.Length < _options.MinPasswordLength) errors["password"] = [$"Use at least {_options.MinPasswordLength} characters."];
        else if (password.Length > _options.MaxPasswordLength) errors["password"] = [$"Use at most {_options.MaxPasswordLength} characters."];
        else if (string.Equals(password, email, StringComparison.OrdinalIgnoreCase)) errors["password"] = ["The password must not be your email."];
        if (errors.Count > 0) return Error.Validation("auth.invalid_registration", "The registration details are invalid.", errors);

        var user = new UserAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = email,
            NormalizedEmail = NormalizeEmail(email),
            PasswordHash = _hasher.Hash(password),
            Roles = (roles ?? _options.DefaultRoles).Distinct(StringComparer.Ordinal).ToList(),
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            SecurityStamp = NewStamp(),
            CreatedAt = _time.GetUtcNow(),
        };

        if (!await _users.TryCreateAsync(user, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict("auth.email_taken", "An account with this email already exists.");
        }

        return user;
    }

    /// <summary>
    /// Checks credentials. After <see cref="AuthOptions.MaxFailedAttempts"/> consecutive failures the account is
    /// locked for <see cref="AuthOptions.LockoutDuration"/>; during a lockout even the right password is refused.
    /// Unknown emails cost the same hashing time as known ones.
    /// </summary>
    public async Task<Result<UserAccount>> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        password ??= string.Empty;
        if (password.Length > _options.MaxPasswordLength) return InvalidCredentials();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var user = await _users.FindByEmailAsync(NormalizeEmail(email), cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                _hasher.Verify(_dummyHash.Value, password);
                return InvalidCredentials();
            }

            var now = _time.GetUtcNow();
            if (user.LockoutEndsAt is { } ends && ends > now) return LockedOut(ends - now);

            var check = _hasher.Verify(user.PasswordHash, password);
            UserAccount updated;
            Result<UserAccount> outcome;
            if (check == PasswordCheck.Failed)
            {
                var previous = user.LockoutEndsAt is null ? user.FailedLoginCount : 0;
                var failures = previous + 1;
                if (failures >= _options.MaxFailedAttempts)
                {
                    updated = user with { FailedLoginCount = 0, LockoutEndsAt = now + _options.LockoutDuration };
                    outcome = LockedOut(_options.LockoutDuration);
                }
                else
                {
                    updated = user with { FailedLoginCount = failures, LockoutEndsAt = null };
                    outcome = InvalidCredentials();
                }
            }
            else
            {
                updated = user with
                {
                    FailedLoginCount = 0,
                    LockoutEndsAt = null,
                    PasswordHash = check == PasswordCheck.SuccessRehashNeeded ? _hasher.Hash(password) : user.PasswordHash,
                };
                outcome = updated;
            }

            if (updated == user || await _users.TryUpdateAsync(updated, cancellationToken).ConfigureAwait(false))
            {
                return outcome.IsSuccess ? (await _users.FindByIdAsync(user.Id, cancellationToken).ConfigureAwait(false))! : outcome;
            }
        }

        return Error.Conflict("auth.busy", "The account is busy. Try again.");
    }

    /// <summary>Returns the user when the refresh token's stamp is still current.</summary>
    public async Task<Result<UserAccount>> ValidateRefreshAsync(string userId, string? stamp, CancellationToken cancellationToken = default)
    {
        var user = await _users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || stamp is null || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(user.SecurityStamp), System.Text.Encoding.UTF8.GetBytes(stamp)))
        {
            return Error.Unauthorized("auth.invalid_refresh_token", "The refresh token is invalid or expired.");
        }

        if (user.LockoutEndsAt is { } ends && ends > _time.GetUtcNow()) return LockedOut(ends - _time.GetUtcNow());
        return user;
    }

    /// <summary>Rotates the security stamp so every token issued before now stops working.</summary>
    public async Task<Result> LogoutEverywhereAsync(string userId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var user = await _users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
            if (user is null) return Error.NotFound("auth.user_not_found", "The user does not exist.");
            if (await _users.TryUpdateAsync(user with { SecurityStamp = NewStamp() }, cancellationToken).ConfigureAwait(false)) return Result.Success();
        }

        return Error.Conflict("auth.busy", "The account is busy. Try again.");
    }

    /// <summary>Builds the principal carried by the tokens.</summary>
    public static ClaimsPrincipal CreatePrincipal(UserAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Email),
            new(ClaimTypes.Email, user.Email),
            new(AuthClaims.SecurityStamp, user.SecurityStamp),
        };
        claims.AddRange(user.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (user.TenantId is not null) claims.Add(new Claim(ClaimsPrincipalExtensions.TenantClaim, user.TenantId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthClaims.IdentityType));
    }

    private static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static Error InvalidCredentials() => Error.Unauthorized("auth.invalid_credentials", "The email or password is incorrect.");

    private static Error LockedOut(TimeSpan retryAfter) =>
        Error.Unauthorized("auth.locked_out", "Too many failed attempts. Try again later.")
            .WithMetadata("retryAfterSeconds", (int)Math.Ceiling(retryAfter.TotalSeconds));
}
