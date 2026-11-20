using System.Net;
using System.Text.Json;
using Agentic.Blocks.Auth;
using Agentic.Blocks.Testing;
using Agentic.Blocks.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentic.Blocks.Auth.Tests;

public class AuthEndpointTests
{
    private const string ExamplePassword = "a-long-demo-password";

    private static Task<KestrelTestHost> StartAsync(ManualTimeProvider clock, Action<AuthOptions>? configure = null, string env = "Development") =>
        KestrelTestHost.StartAsync(
            b => b.Services.AddSingleton<TimeProvider>(clock).AddBlocksWeb().AddBlocksAuth(o =>
            {
                o.Hashing.Iterations = 1_000;
                o.QueryStringTokenPaths.Add("/hub");
                configure?.Invoke(o);
            }),
            app =>
            {
                app.UseBlocksWeb();
                app.MapBlocksAuth("/auth");
                app.MapGet("/admin", () => "secret").RequireAuthorization(p => p.RequireRole("admin"));
                app.MapGet("/hub/ping", (HttpContext c) => c.User.GetUserId()).RequireAuthorization();
            },
            env);

    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private static async Task<(string Access, string Refresh)> RegisterAndLoginAsync(KestrelTestHost host, string email = "grace@example.com")
    {
        var reg = await host.SendAsync(HttpMethod.Post, "/auth/register", new { email, password = ExamplePassword });
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
        var login = await host.SendAsync(HttpMethod.Post, "/auth/login", new { email, password = ExamplePassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await Json(login);
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.Equal(900, body.GetProperty("expiresIn").GetInt32());
        return (body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!);
    }

    [Fact]
    public async Task Register_login_me_flow()
    {
        await using var host = await StartAsync(new ManualTimeProvider());
        var (access, _) = await RegisterAndLoginAsync(host);
        var me = await host.SendAsync(HttpMethod.Get, "/auth/me", bearer: access);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await Json(me);
        Assert.Equal("grace@example.com", body.GetProperty("email").GetString());
        Assert.Equal("user", body.GetProperty("roles")[0].GetString());
    }

    [Fact]
    public async Task Me_without_token_is_401()
    {
        await using var host = await StartAsync(new ManualTimeProvider());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_401_problem()
    {
        await using var host = await StartAsync(new ManualTimeProvider());
        await RegisterAndLoginAsync(host);
        var r = await host.SendAsync(HttpMethod.Post, "/auth/login", new { email = "grace@example.com", password = "nope-nope-nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("auth.invalid_credentials", (await Json(r)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Invalid_registration_is_400_with_field_errors()
    {
        await using var host = await StartAsync(new ManualTimeProvider());
        var r = await host.SendAsync(HttpMethod.Post, "/auth/register", new { email = "bad", password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var errors = (await Json(r)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Access_token_expires_and_refresh_issues_new_one()
    {
        var clock = new ManualTimeProvider();
        await using var host = await StartAsync(clock);
        var (access, refresh) = await RegisterAndLoginAsync(host);
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/auth/me", bearer: access)).StatusCode);
        var refreshed = await host.SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var newAccess = (await Json(refreshed)).GetProperty("accessToken").GetString();
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/auth/me", bearer: newAccess)).StatusCode);
    }

    [Fact]
    public async Task Expired_or_garbage_refresh_token_is_401()
    {
        var clock = new ManualTimeProvider();
        await using var host = await StartAsync(clock);
        var (_, refresh) = await RegisterAndLoginAsync(host);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = "garbage" })).StatusCode);
        clock.Advance(TimeSpan.FromDays(15));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = refresh })).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_access_and_refresh_tokens()
    {
        await using var host = await StartAsync(new ManualTimeProvider());
        var (access, refresh) = await RegisterAndLoginAsync(host);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync(HttpMethod.Post, "/auth/logout", bearer: access)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Get, "/auth/me", bearer: access)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Post, "/auth/refresh", new { refreshToken = refresh })).StatusCode);
    }

    [Fact]
    public async Task Role_policy_is_enforced()
    {
        await using var host = await StartAsync(new ManualTimeProvider());
        var (access, _) = await RegisterAndLoginAsync(host);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Get, "/admin", bearer: access)).StatusCode);
    }

    [Fact]
    public async Task Demo_users_are_seeded_in_development_only()
    {
        void Demo(AuthOptions o)
        {
            var demo = new DemoUser { Email = "demo@example.com", Password = ExamplePassword, TenantId = "demo-tenant" };
            demo.Roles.Add("admin");
            o.DemoUsers.Add(demo);
        }

        await using (var dev = await StartAsync(new ManualTimeProvider(), Demo))
        {
            var login = await dev.SendAsync(HttpMethod.Post, "/auth/login", new { email = "demo@example.com", password = ExamplePassword });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var access = (await Json(login)).GetProperty("accessToken").GetString();
            var me = await Json(await dev.SendAsync(HttpMethod.Get, "/auth/me", bearer: access));
            Assert.Equal("demo-tenant", me.GetProperty("tenantId").GetString());
            Assert.Equal(HttpStatusCode.OK, (await dev.SendAsync(HttpMethod.Get, "/admin", bearer: access)).StatusCode);
        }

        await using var prod = await StartAsync(new ManualTimeProvider(), Demo, env: "Production");
        var refused = await prod.SendAsync(HttpMethod.Post, "/auth/login", new { email = "demo@example.com", password = ExamplePassword });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Query_string_token_is_accepted_only_on_configured_paths()
    {
        await using var host = await StartAsync(new ManualTimeProvider());
        var (access, _) = await RegisterAndLoginAsync(host);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync($"/hub/ping?access_token={Uri.EscapeDataString(access)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync($"/auth/me?access_token={Uri.EscapeDataString(access)}")).StatusCode);
    }

    [Fact]
    public async Task Map_without_web_block_fails_fast()
    {
        var ex = await Record(async () =>
        {
            await using var host = await KestrelTestHost.StartAsync(b => b.Services.AddBlocksAuth(), app => app.MapBlocksAuth());
        });
        Assert.True(ex is InvalidOperationException);
        Assert.Contains("AddBlocksWeb", ex!.Message);
    }

    private static async Task<Exception?> Record(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }
}
