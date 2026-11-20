using System.Net;
using System.Text.Json;
using Agentic.Blocks.Core;
using Agentic.Blocks.Testing;
using Agentic.Blocks.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentic.Blocks.Web.Tests;

public class ErrorMappingTests
{
    [Theory]
    [InlineData(ErrorType.Validation, 400)]
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.PaymentRequired, 402)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.Failure, 500)]
    public void Maps_error_type_to_status(ErrorType type, int status)
    {
        Assert.Equal(status, ErrorHttpMapping.StatusCodeFor(type));
    }

    [Fact]
    public void Correlation_id_validation_rejects_injection()
    {
        Assert.True(CorrelationContext.IsValid("abc-123_x.y:z"));
        Assert.False(CorrelationContext.IsValid("bad value\r\nSet-Cookie: x"));
        Assert.False(CorrelationContext.IsValid(new string('a', 129)));
        Assert.False(CorrelationContext.IsValid(""));
    }

    [Fact]
    public void Correlation_context_is_scoped_to_async_flow()
    {
        var ctx = new CorrelationContext();
        Assert.Null(ctx.CorrelationId);
        using (CorrelationContext.Begin("outer"))
        {
            using (CorrelationContext.Begin("inner")) Assert.Equal("inner", ctx.CorrelationId);
            Assert.Equal("outer", ctx.CorrelationId);
        }

        Assert.Null(ctx.CorrelationId);
    }
}

public class WebPipelineTests
{
    private static Task<KestrelTestHost> StartAsync(Action<BlocksWebOptions>? configure = null) =>
        KestrelTestHost.StartAsync(
            b => b.Services.AddBlocksWeb(configure),
            app =>
            {
                app.UseBlocksWeb();
                app.MapGet("/ok", () => Result.Success(new { Name = "demo" }).ToHttpResult());
                app.MapGet("/missing", () => Result.Failure<string>(Error.NotFound("thing.not_found", "No such thing.")).ToHttpResult());
                app.MapGet("/invalid", () => Result.Failure<string>(Error.Validation("thing.invalid", "name", "Name is required.")).ToHttpResult());
                app.MapGet("/pay", () => Result.Failure(Error.PaymentRequired("plan.required", "Upgrade.").WithMetadata("renewUrl", "/plans")).ToHttpResult());
                app.MapGet("/nocontent", () => Result.Success().ToHttpResult());
                app.MapGet("/created", () => Result.Success(7).ToHttpResult(id => Results.Created($"/things/{id}", new { id })));
                app.MapGet("/boom", (HttpContext _) => { throw new InvalidOperationException("secret internals"); });
                app.MapGet("/correlation", (ICorrelationContext c) => c.CorrelationId);
                app.MapGet("/limited", () => "ok").RequireRateLimiting(BlocksRateLimitPolicies.Auth);
            });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Success_returns_200_with_value()
    {
        await using var host = await StartAsync();
        var response = await host.Client.GetAsync("/ok");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("demo", (await JsonAsync(response)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Not_found_error_becomes_problem_details_with_code_and_correlation()
    {
        await using var host = await StartAsync();
        var response = await host.SendAsync(HttpMethod.Get, "/missing", headers: new Dictionary<string, string> { ["X-Correlation-Id"] = "corr-42" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await JsonAsync(response);
        Assert.Equal("thing.not_found", body.GetProperty("code").GetString());
        Assert.Equal("corr-42", body.GetProperty("correlationId").GetString());
        Assert.True(body.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Validation_error_has_field_errors()
    {
        await using var host = await StartAsync();
        var body = await JsonAsync(await host.Client.GetAsync("/invalid"));
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("Name is required.", body.GetProperty("errors").GetProperty("name")[0].GetString());
    }

    [Fact]
    public async Task Metadata_becomes_problem_extension()
    {
        await using var host = await StartAsync();
        var response = await host.Client.GetAsync("/pay");
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        Assert.Equal("/plans", (await JsonAsync(response)).GetProperty("renewUrl").GetString());
    }

    [Fact]
    public async Task Custom_success_and_no_content_mappings()
    {
        await using var host = await StartAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.GetAsync("/nocontent")).StatusCode);
        var created = await host.Client.GetAsync("/created");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/things/7", created.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Unhandled_exception_is_problem_details_without_internals()
    {
        await using var host = await StartAsync();
        var response = await host.Client.GetAsync("/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret internals", text);
        Assert.Contains("correlationId", text);
    }

    [Fact]
    public async Task Correlation_id_is_echoed_and_available_to_handlers()
    {
        await using var host = await StartAsync();
        var response = await host.SendAsync(HttpMethod.Get, "/correlation", headers: new Dictionary<string, string> { ["X-Correlation-Id"] = "abc-123" });
        Assert.Equal("abc-123", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("abc-123", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Correlation_id_is_generated_when_missing_or_invalid()
    {
        await using var host = await StartAsync();
        var response = await host.SendAsync(HttpMethod.Get, "/correlation", headers: new Dictionary<string, string> { ["X-Correlation-Id"] = "<script>" });
        var id = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.True(CorrelationContext.IsValid(id));
        Assert.False(id == "<script>");
    }

    [Fact]
    public async Task Health_endpoints_are_mapped()
    {
        await using var host = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/health/live")).StatusCode);
        var ready = await host.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Auth_policy_rejects_with_429_problem_and_retry_after()
    {
        await using var host = await StartAsync(o => { o.AuthPermitLimit = 2; o.AuthWindow = TimeSpan.FromMinutes(1); });
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/limited")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/limited")).StatusCode);
        var rejected = await host.Client.GetAsync("/limited");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));
        Assert.Equal("rate_limit.exceeded", (await JsonAsync(rejected)).GetProperty("code").GetString());
    }

    [Fact]
    public void EnsureBlocksWeb_throws_helpful_error_without_registration()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var ex = Assert.Throws<InvalidOperationException>(() => provider.EnsureBlocksWeb("Auth"));
        Assert.Contains("AddBlocksWeb", ex.Message);
    }
}

public class ClaimsTests
{
    [Fact]
    public void GetUserId_reads_name_identifier_or_sub()
    {
        var a = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "u1"), new System.Security.Claims.Claim("tenant_id", "t1")], "test"));
        var b = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("sub", "u2")], "oidc"));
        var anonymous = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        Assert.Equal("u1", a.GetUserId());
        Assert.Equal("t1", a.GetTenantId());
        Assert.Equal("u2", b.GetUserId());
        Assert.Null(anonymous.GetUserId());
    }
}
