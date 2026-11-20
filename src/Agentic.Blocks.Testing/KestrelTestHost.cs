using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentic.Blocks.Testing;

/// <summary>
/// Runs a real <see cref="WebApplication"/> on a random loopback port for end-to-end HTTP tests, using only the
/// ASP.NET Core shared framework (no TestHost package). Dispose it to stop the server.
/// </summary>
public sealed class KestrelTestHost : IAsyncDisposable
{
    private KestrelTestHost(WebApplication app, HttpClient client)
    {
        App = app;
        Client = client;
    }

    /// <summary>The running application (use <c>App.Services</c> to reach singletons).</summary>
    public WebApplication App { get; }

    /// <summary>A client whose base address is the server.</summary>
    public HttpClient Client { get; }

    /// <summary>Builds and starts an app. <paramref name="configureServices"/> runs before Build, <paramref name="configureApp"/> after.</summary>
    public static async Task<KestrelTestHost> StartAsync(
        Action<WebApplicationBuilder> configureServices,
        Action<WebApplication> configureApp,
        string environment = "Development")
    {
        ArgumentNullException.ThrowIfNull(configureServices);
        ArgumentNullException.ThrowIfNull(configureApp);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        configureServices(builder);
        var app = builder.Build();
        configureApp(app);
        await app.StartAsync().ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new KestrelTestHost(app, client);
    }

    /// <summary>Sends a request with optional JSON body, bearer token and headers.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? json = null, string? bearer = null,
        IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null) request.Content = JsonContent.Create(json);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (headers is not null)
        {
            foreach (var (key, value) in headers) request.Headers.TryAddWithoutValidation(key, value);
        }

        return await Client.SendAsync(request).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync().ConfigureAwait(false);
        await App.DisposeAsync().ConfigureAwait(false);
    }
}
