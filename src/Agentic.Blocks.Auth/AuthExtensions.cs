using System.Security.Claims;
using Agentic.Blocks.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentic.Blocks.Auth;

/// <summary>Register request.</summary>
/// <param name="Email">Email.</param>
/// <param name="Password">Password.</param>
public sealed record RegisterRequest(string Email, string Password);

/// <summary>Login request.</summary>
/// <param name="Email">Email.</param>
/// <param name="Password">Password.</param>
public sealed record LoginRequest(string Email, string Password);

/// <summary>Refresh request.</summary>
/// <param name="RefreshToken">The refresh token from login.</param>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>The current user.</summary>
/// <param name="Id">User id.</param>
/// <param name="Email">Email.</param>
/// <param name="Roles">Roles.</param>
/// <param name="TenantId">Tenant, if any.</param>
public sealed record MeResponse(string Id, string Email, IReadOnlyList<string> Roles, string? TenantId);

/// <summary>Registration and endpoint extensions for the Auth block.</summary>
public static partial class AuthExtensions
{
    /// <summary>
    /// Registers bearer-token authentication (opaque, data-protected tokens), authorization, <see cref="AuthService"/>,
    /// PBKDF2 hashing, the in-memory <see cref="IUserStore"/> (replace it for production), per-request stamp validation
    /// and Development-only demo seeding.
    /// </summary>
    public static IServiceCollection AddBlocksAuth(this IServiceCollection services, Action<AuthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var optionsBuilder = services.AddOptions<AuthOptions>()
            .Validate(o => o.MaxFailedAttempts >= 1 && o.LockoutDuration > TimeSpan.Zero && o.MinPasswordLength >= 8 && o.Hashing.Iterations >= 1 && o.Hashing.SaltSize >= 16 && o.Hashing.KeySize >= 16,
                "Auth options are invalid.");
        if (configure is not null) optionsBuilder.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IUserStore, InMemoryUserStore>();
        services.TryAddSingleton<IPasswordHasher>(sp => new Pbkdf2PasswordHasher(sp.GetRequiredService<IOptions<AuthOptions>>().Value.Hashing));
        services.TryAddSingleton<AuthService>();
        services.AddTransient<IClaimsTransformation, SecurityStampValidator>();
        services.AddHostedService<DemoUserSeeder>();

        services.AddAuthentication(BearerTokenDefaults.AuthenticationScheme).AddBearerToken();
        services.AddOptions<BearerTokenOptions>(BearerTokenDefaults.AuthenticationScheme).Configure<IOptions<AuthOptions>>((bearer, auth) =>
        {
            bearer.BearerTokenExpiration = auth.Value.AccessTokenLifetime;
            bearer.RefreshTokenExpiration = auth.Value.RefreshTokenLifetime;
            var paths = auth.Value.QueryStringTokenPaths.ToList();
            bearer.Events.OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"].ToString();
                if (!string.IsNullOrEmpty(token) && paths.Exists(p => ctx.HttpContext.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
                {
                    ctx.Token = token;
                }

                return Task.CompletedTask;
            };
        });
        services.TryAddSingleton<IAuthorizationMiddlewareResultHandler, StaleTokenResultHandler>();
        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// Maps <c>POST register</c>, <c>POST login</c>, <c>POST refresh</c>, <c>POST logout</c> and <c>GET me</c> under
    /// <paramref name="prefix"/>. Login and refresh return <c>{ tokenType, accessToken, expiresIn, refreshToken }</c>.
    /// </summary>
    public static RouteGroupBuilder MapBlocksAuth(this IEndpointRouteBuilder endpoints, string prefix = "/auth")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.ServiceProvider.EnsureBlocksWeb("Agentic.Blocks.Auth");
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        var group = endpoints.MapGroup(prefix).WithTags("Auth");

        var register = group.MapPost("/register", async (RegisterRequest request, AuthService auth, CancellationToken ct) =>
            (await auth.RegisterAsync(request.Email, request.Password, cancellationToken: ct).ConfigureAwait(false))
                .ToHttpResult(user => Results.Created($"{prefix}/me", ToMe(user))));

        var login = group.MapPost("/login", async (LoginRequest request, AuthService auth, CancellationToken ct) =>
        {
            var result = await auth.LoginAsync(request.Email, request.Password, ct).ConfigureAwait(false);
            return result.IsSuccess
                ? Results.SignIn(AuthService.CreatePrincipal(result.Value), authenticationScheme: BearerTokenDefaults.AuthenticationScheme)
                : result.Error.ToProblem();
        });

        var refresh = group.MapPost("/refresh", async (RefreshRequest request, AuthService auth, IOptionsMonitor<BearerTokenOptions> bearer, TimeProvider time,
            CancellationToken ct) =>
        {
            var protector = bearer.Get(BearerTokenDefaults.AuthenticationScheme).RefreshTokenProtector;
            var ticket = string.IsNullOrEmpty(request.RefreshToken) ? null : protector.Unprotect(request.RefreshToken);
            var userId = ticket?.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (ticket?.Properties.ExpiresUtc is not { } expires || time.GetUtcNow() >= expires || userId is null)
            {
                return Core.Error.Unauthorized("auth.invalid_refresh_token", "The refresh token is invalid or expired.").ToProblem();
            }

            var result = await auth.ValidateRefreshAsync(userId, ticket.Principal.FindFirstValue(AuthClaims.SecurityStamp), ct).ConfigureAwait(false);
            return result.IsSuccess
                ? Results.SignIn(AuthService.CreatePrincipal(result.Value), authenticationScheme: BearerTokenDefaults.AuthenticationScheme)
                : result.Error.ToProblem();
        });

        group.MapPost("/logout", async (ClaimsPrincipal user, AuthService auth, CancellationToken ct) =>
            (await auth.LogoutEverywhereAsync(user.GetUserId()!, ct).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization();

        group.MapGet("/me", async (ClaimsPrincipal principal, IUserStore users, CancellationToken ct) =>
        {
            var user = await users.FindByIdAsync(principal.GetUserId()!, ct).ConfigureAwait(false);
            return user is null ? Results.Unauthorized() : Results.Ok(ToMe(user));
        }).RequireAuthorization();

        if (options.RateLimitPolicy is { } policy)
        {
            register.RequireRateLimiting(policy);
            login.RequireRateLimiting(policy);
            refresh.RequireRateLimiting(policy);
        }

        return group;
    }

    private static MeResponse ToMe(UserAccount user) => new(user.Id, user.Email, user.Roles, user.TenantId);

    /// <summary>A token revoked by logout authenticates but carries an anonymous principal: answer 401, not 403.</summary>
    private sealed class StaleTokenResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _default = new();

        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Forbidden && context.User.Identity?.IsAuthenticated != true) authorizeResult = PolicyAuthorizationResult.Challenge();
            return _default.HandleAsync(next, context, policy, authorizeResult);
        }
    }

    /// <summary>Turns tokens with a stale security stamp (after logout) into an anonymous principal.</summary>
    private sealed class SecurityStampValidator(IUserStore users, IOptions<AuthOptions> options) : IClaimsTransformation
    {
        public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            if (!options.Value.ValidateSecurityStampOnEachRequest) return principal;
            var identity = principal.Identities.FirstOrDefault(i => i.AuthenticationType == AuthClaims.IdentityType);
            var stamp = identity?.FindFirst(AuthClaims.SecurityStamp)?.Value;
            var id = identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (identity is null || stamp is null || id is null) return principal;
            var user = await users.FindByIdAsync(id).ConfigureAwait(false);
            return user is not null && user.SecurityStamp == stamp ? principal : new ClaimsPrincipal(new ClaimsIdentity());
        }
    }

    private sealed partial class DemoUserSeeder(IHostEnvironment env, AuthService auth, IOptions<AuthOptions> options, ILogger<DemoUserSeeder> logger) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (!env.IsDevelopment()) return;
            foreach (var demo in options.Value.DemoUsers)
            {
                var generated = string.IsNullOrEmpty(demo.Password);
                var secret = generated ? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(15)) : demo.Password!;
                var roles = demo.Roles.Count > 0 ? demo.Roles : null;
                var result = await auth.RegisterAsync(demo.Email, secret, roles, demo.TenantId, cancellationToken).ConfigureAwait(false);
                if (result.IsFailure) continue;
                if (generated) LogGenerated(logger, demo.Email, secret);
                else LogSeeded(logger, demo.Email);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        [LoggerMessage(Level = LogLevel.Warning, Message = "Development only: seeded demo user {Email} with generated password {Password}")]
        private static partial void LogGenerated(ILogger logger, string email, string password);

        [LoggerMessage(Level = LogLevel.Information, Message = "Development only: seeded demo user {Email}")]
        private static partial void LogSeeded(ILogger logger, string email);
    }
}
