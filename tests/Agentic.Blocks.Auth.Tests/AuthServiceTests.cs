using Agentic.Blocks.Auth;
using Agentic.Blocks.Core;
using Agentic.Blocks.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agentic.Blocks.Auth.Tests;

public class AuthServiceTests
{
    private const string Email = "ada@example.com";
    private const string Password = "a-long-demo-password";
    private readonly ManualTimeProvider _clock = new();
    private readonly InMemoryUserStore _store = new();
    private readonly AuthOptions _options = new();

    public AuthServiceTests() => _options.Hashing.Iterations = 1_000;

    private AuthService Service(int? iterations = null)
    {
        if (iterations is not null) _options.Hashing.Iterations = iterations.Value;
        return new AuthService(_store, new Pbkdf2PasswordHasher(_options.Hashing), _clock, Options.Create(_options));
    }

    private async Task<AuthService> RegisteredAsync()
    {
        var svc = Service();
        Assert.True((await svc.RegisterAsync(Email, Password)).IsSuccess);
        return svc;
    }

    [Fact]
    public async Task Register_creates_user_with_default_role_and_hashed_password()
    {
        var user = (await Service().RegisterAsync(Email, Password)).Value;
        Assert.Equal(["user"], user.Roles);
        Assert.StartsWith("v1$pbkdf2-sha256$", user.PasswordHash);
        Assert.DoesNotContain(Password, user.PasswordHash);
    }

    [Fact]
    public async Task Register_duplicate_email_is_conflict_case_insensitive()
    {
        var svc = await RegisteredAsync();
        var result = await svc.RegisterAsync("ADA@example.com", Password);
        Assert.Equal("auth.email_taken", result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Theory]
    [InlineData("not-an-email", "a-long-demo-password", "email")]
    [InlineData("ada@example.com", "short", "password")]
    [InlineData("ada@example.com", "ada@example.com", "password")]
    public async Task Register_validates_input(string email, string password, string field)
    {
        var result = await Service().RegisterAsync(email, password);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.True(result.Error.FieldErrors.ContainsKey(field));
    }

    [Fact]
    public async Task Register_rejects_overlong_password()
    {
        var result = await Service().RegisterAsync(Email, new string('x', 129));
        Assert.True(result.Error.FieldErrors.ContainsKey("password"));
    }

    [Fact]
    public async Task Login_with_correct_password_succeeds()
    {
        var svc = await RegisteredAsync();
        Assert.True((await svc.LoginAsync(" Ada@Example.com ", Password)).IsSuccess);
    }

    [Fact]
    public async Task Unknown_email_and_wrong_password_give_same_error()
    {
        var svc = await RegisteredAsync();
        var unknown = await svc.LoginAsync("nobody@example.com", Password);
        var wrong = await svc.LoginAsync(Email, "wrong-password-123");
        Assert.Equal("auth.invalid_credentials", unknown.Error.Code);
        Assert.Equal(unknown.Error.Code, wrong.Error.Code);
        Assert.Equal(unknown.Error.Message, wrong.Error.Message);
    }

    [Fact]
    public async Task Four_failures_then_correct_password_still_works_and_resets_counter()
    {
        var svc = await RegisteredAsync();
        for (var i = 0; i < 4; i++) await svc.LoginAsync(Email, "wrong-password-123");
        Assert.True((await svc.LoginAsync(Email, Password)).IsSuccess);
        Assert.Equal(0, (await _store.FindByEmailAsync(AuthService.NormalizeEmail(Email)))!.FailedLoginCount);
    }

    [Fact]
    public async Task Fifth_failure_locks_account_for_15_minutes()
    {
        var svc = await RegisteredAsync();
        for (var i = 0; i < 4; i++) Assert.Equal("auth.invalid_credentials", (await svc.LoginAsync(Email, "wrong-password-123")).Error.Code);
        var fifth = await svc.LoginAsync(Email, "wrong-password-123");
        Assert.Equal("auth.locked_out", fifth.Error.Code);
        Assert.Equal(900, fifth.Error.Metadata["retryAfterSeconds"]);

        Assert.Equal("auth.locked_out", (await svc.LoginAsync(Email, Password)).Error.Code);
        _clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.Equal("auth.locked_out", (await svc.LoginAsync(Email, Password)).Error.Code);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await svc.LoginAsync(Email, Password)).IsSuccess);
    }

    [Fact]
    public async Task Attempts_during_lockout_do_not_extend_it()
    {
        var svc = await RegisteredAsync();
        for (var i = 0; i < 5; i++) await svc.LoginAsync(Email, "wrong-password-123");
        _clock.Advance(TimeSpan.FromMinutes(10));
        for (var i = 0; i < 10; i++) await svc.LoginAsync(Email, "wrong-password-123");
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True((await svc.LoginAsync(Email, Password)).IsSuccess);
    }

    [Fact]
    public async Task After_lockout_ends_counter_starts_fresh()
    {
        var svc = await RegisteredAsync();
        for (var i = 0; i < 5; i++) await svc.LoginAsync(Email, "wrong-password-123");
        _clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal("auth.invalid_credentials", (await svc.LoginAsync(Email, "wrong-password-123")).Error.Code);
        var user = (await _store.FindByEmailAsync(AuthService.NormalizeEmail(Email)))!;
        Assert.Equal(1, user.FailedLoginCount);
        Assert.Null(user.LockoutEndsAt);
    }

    [Fact]
    public async Task Login_rehashes_when_parameters_change()
    {
        await RegisteredAsync();
        var before = (await _store.FindByEmailAsync(AuthService.NormalizeEmail(Email)))!.PasswordHash;
        var upgraded = Service(iterations: 2_000);
        Assert.True((await upgraded.LoginAsync(Email, Password)).IsSuccess);
        var after = (await _store.FindByEmailAsync(AuthService.NormalizeEmail(Email)))!.PasswordHash;
        Assert.Contains("$1000$", before);
        Assert.Contains("$2000$", after);
        Assert.True((await upgraded.LoginAsync(Email, Password)).IsSuccess);
    }

    [Fact]
    public async Task Logout_rotates_stamp_and_invalidates_refresh()
    {
        var svc = await RegisteredAsync();
        var user = (await svc.LoginAsync(Email, Password)).Value;
        Assert.True((await svc.ValidateRefreshAsync(user.Id, user.SecurityStamp)).IsSuccess);
        Assert.True((await svc.LogoutEverywhereAsync(user.Id)).IsSuccess);
        Assert.Equal("auth.invalid_refresh_token", (await svc.ValidateRefreshAsync(user.Id, user.SecurityStamp)).Error.Code);
    }

    [Fact]
    public async Task Principal_carries_roles_tenant_and_stamp()
    {
        var user = (await Service().RegisterAsync(Email, Password, ["admin", "user"], "tenant-7")).Value;
        var principal = AuthService.CreatePrincipal(user);
        Assert.True(principal.IsInRole("admin"));
        Assert.Equal("tenant-7", principal.FindFirst("tenant_id")?.Value);
        Assert.Equal(user.SecurityStamp, principal.FindFirst(AuthClaims.SecurityStamp)?.Value);
        Assert.Equal(AuthClaims.IdentityType, principal.Identity?.AuthenticationType);
    }

    [Fact]
    public async Task Concurrent_failures_are_all_counted()
    {
        var svc = await RegisteredAsync();
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => svc.LoginAsync(Email, "wrong-password-123"))));
        Assert.Equal("auth.locked_out", (await svc.LoginAsync(Email, Password)).Error.Code);
    }
}
