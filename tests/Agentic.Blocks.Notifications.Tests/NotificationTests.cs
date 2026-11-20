using System.Net;
using System.Text.Json;
using Agentic.Blocks.Core;
using Agentic.Blocks.Notifications;
using Agentic.Blocks.Outbox;
using Agentic.Blocks.Testing;
using Agentic.Blocks.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Agentic.Blocks.Notifications.Tests;

public sealed class FakeEmail : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];
    public int FailuresLeft { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (FailuresLeft > 0) { FailuresLeft--; throw new InvalidOperationException("smtp down"); }
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class FakeSms : ISmsSender
{
    public List<SmsMessage> Sent { get; } = [];

    public Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class FakePusher : INotificationPusher
{
    public List<(string User, InboxItem Item, int Unread)> Pushes { get; } = [];

    public Task PushAsync(string userId, InboxItem item, int unreadCount, CancellationToken cancellationToken = default)
    {
        Pushes.Add((userId, item, unreadCount));
        return Task.CompletedTask;
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = [];
    public ILogger CreateLogger(string categoryName) => new L(Lines);
    public void Dispose() { }

    private sealed class L(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lines) lines.Add(formatter(state, exception));
        }
    }
}

public sealed class NotificationFixture : IDisposable
{
    public NotificationFixture(bool fakeSenders = true)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<TimeProvider>(Clock);
        if (fakeSenders)
        {
            services.AddSingleton<IEmailSender>(Email);
            services.AddSingleton<ISmsSender>(Sms);
        }

        services.AddSingleton<INotificationPusher>(Pusher);
        services.AddBlocksOutbox(o => { o.RunDispatcher = false; o.JitterFactor = 0; o.BaseDelay = TimeSpan.FromSeconds(5); });
        services.AddBlocksNotifications(o => o
            .AddTemplate("welcome", "Welcome, {{name}}", "Hi {{name}}, your plan is {{plan}}.")
            .SetCategoryDefaults("billing", NotificationChannel.InApp, NotificationChannel.Email, NotificationChannel.Sms));
        Provider = services.BuildServiceProvider();
        Provider.GetRequiredService<InMemoryRecipientDirectory>().Set("u-1", "ada@example.com", "+15550101234");
    }

    public ManualTimeProvider Clock { get; } = new();
    public FakeEmail Email { get; } = new();
    public FakeSms Sms { get; } = new();
    public FakePusher Pusher { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();
    public ServiceProvider Provider { get; }
    public IInboxStore Inbox => Provider.GetRequiredService<IInboxStore>();

    public async Task<Result<Guid>> SendAsync(string user, string category, string template = "welcome")
    {
        using var scope = Provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<INotificationService>()
            .SendAsync(user, category, template, new Dictionary<string, string> { ["name"] = "Ada", ["plan"] = "Plus" });
        await scope.ServiceProvider.GetRequiredService<IOutbox>().CommitAsync();
        return result;
    }

    public async Task DrainAsync(int rounds = 4)
    {
        var processor = Provider.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < rounds; i++) await processor.ProcessDueAsync();
    }

    public void Dispose() => Provider.Dispose();
}

public class NotificationDeliveryTests
{
    [Fact]
    public async Task Send_only_stages_until_outbox_runs()
    {
        using var f = new NotificationFixture();
        Assert.True((await f.SendAsync("u-1", "billing")).IsSuccess);
        Assert.Equal(0, await f.Inbox.UnreadCountAsync("u-1"));
        Assert.Empty(f.Email.Sent);
    }

    [Fact]
    public async Task Delivers_on_every_default_channel_of_category()
    {
        using var f = new NotificationFixture();
        var id = (await f.SendAsync("u-1", "billing")).Value;
        await f.DrainAsync();
        var page = (await f.Inbox.PageAsync("u-1", new PageRequest())).Value;
        var item = Assert.Single(page.Items);
        Assert.Equal(id, item.Id);
        Assert.Equal("Welcome, Ada", item.Title);
        Assert.Equal("Hi Ada, your plan is Plus.", item.Body);
        Assert.Equal("ada@example.com", Assert.Single(f.Email.Sent).To);
        Assert.Equal("+15550101234", Assert.Single(f.Sms.Sent).To);
        var push = Assert.Single(f.Pusher.Pushes);
        Assert.Equal(1, push.Unread);
    }

    [Fact]
    public async Task Unconfigured_category_uses_in_app_only()
    {
        using var f = new NotificationFixture();
        await f.SendAsync("u-1", "social");
        await f.DrainAsync();
        Assert.Equal(1, await f.Inbox.UnreadCountAsync("u-1"));
        Assert.Empty(f.Email.Sent);
        Assert.Empty(f.Sms.Sent);
    }

    [Fact]
    public async Task User_preference_overrides_category_default()
    {
        using var f = new NotificationFixture();
        var prefs = f.Provider.GetRequiredService<INotificationPreferenceStore>();
        await prefs.SetAsync("u-1", "billing", NotificationChannel.Sms, false);
        await prefs.SetAsync("u-1", "billing", NotificationChannel.InApp, false);
        await f.SendAsync("u-1", "billing");
        await f.DrainAsync();
        Assert.Empty(f.Sms.Sent);
        Assert.Equal(0, await f.Inbox.UnreadCountAsync("u-1"));
        Assert.Single(f.Email.Sent);
    }

    [Fact]
    public async Task Failing_email_does_not_block_in_app_and_is_retried()
    {
        using var f = new NotificationFixture();
        f.Email.FailuresLeft = 2;
        await f.SendAsync("u-1", "billing");
        await f.DrainAsync();
        Assert.Equal(1, await f.Inbox.UnreadCountAsync("u-1"));
        Assert.Single(f.Sms.Sent);
        Assert.Empty(f.Email.Sent);
        f.Clock.Advance(TimeSpan.FromSeconds(5));
        await f.DrainAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        await f.DrainAsync();
        Assert.Single(f.Email.Sent);
        Assert.Single(f.Sms.Sent);
        Assert.Single(f.Pusher.Pushes);
    }

    [Fact]
    public async Task Redelivered_in_app_message_is_not_duplicated()
    {
        using var f = new NotificationFixture();
        await f.SendAsync("u-1", "social");
        await f.DrainAsync();
        var store = (InMemoryOutboxStore)f.Provider.GetRequiredService<IOutboxStore>();
        var delivery = store.Snapshot().Single(m => m.Type == "notifications.delivery.v1");
        await store.AppendAsync([delivery with { Id = Guid.NewGuid(), Status = OutboxMessageStatus.Pending, Attempts = 0 }]);
        await f.DrainAsync();
        Assert.Equal(1, await f.Inbox.UnreadCountAsync("u-1"));
        Assert.Single(f.Pusher.Pushes);
    }

    [Fact]
    public async Task Unknown_template_and_missing_user_are_validation_errors()
    {
        using var f = new NotificationFixture();
        Assert.Equal("notifications.unknown_template", (await f.SendAsync("u-1", "billing", "nope")).Error.Code);
        Assert.Equal("notifications.invalid", (await f.SendAsync(" ", "billing")).Error.Code);
    }

    [Fact]
    public async Task Missing_contact_details_skip_email_without_failing()
    {
        using var f = new NotificationFixture();
        await f.SendAsync("u-2", "billing");
        await f.DrainAsync();
        Assert.Empty(f.Email.Sent);
        Assert.Equal(1, await f.Inbox.UnreadCountAsync("u-2"));
        var store = (InMemoryOutboxStore)f.Provider.GetRequiredService<IOutboxStore>();
        Assert.All(store.Snapshot(), m => Assert.Equal(OutboxMessageStatus.Succeeded, m.Status));
    }

    [Fact]
    public async Task Development_senders_log_masked_recipient_and_no_content()
    {
        using var f = new NotificationFixture(fakeSenders: false);
        await f.SendAsync("u-1", "billing");
        await f.DrainAsync();
        string all;
        lock (f.Logs.Lines) all = string.Join("\n", f.Logs.Lines);
        Assert.Contains("a***@example.com", all);
        Assert.Contains("*******1234", all);
        Assert.DoesNotContain("ada@example.com", all);
        Assert.DoesNotContain("+15550101234", all);
        Assert.DoesNotContain("Hi Ada", all);
    }
}

public class NotificationUnitTests
{
    [Theory]
    [InlineData("ada@example.com", "a***@example.com")]
    [InlineData("x@example.org", "x***@example.org")]
    [InlineData("broken", "***")]
    [InlineData(null, "(none)")]
    public void Masks_email(string? input, string expected) => Assert.Equal(expected, PersonalDataMasker.MaskEmail(input));

    [Theory]
    [InlineData("+15550101234", "*******1234")]
    [InlineData("123", "****")]
    [InlineData("", "(none)")]
    public void Masks_phone(string input, string expected) => Assert.Equal(expected, PersonalDataMasker.MaskPhone(input));

    [Fact]
    public void Template_renders_and_blanks_unknown_placeholders()
    {
        var t = new NotificationTemplate("k", "Hi {{ name }}", "Plan {{plan}} {{missing}}!");
        var (title, body) = t.Render(new Dictionary<string, string> { ["name"] = "Ada", ["plan"] = "Plus" });
        Assert.Equal("Hi Ada", title);
        Assert.Equal("Plan Plus !", body);
    }

    [Fact]
    public void Effective_channels_merge_defaults_and_overrides()
    {
        var o = new NotificationOptions().SetCategoryDefaults("billing", NotificationChannel.InApp, NotificationChannel.Email);
        var overrides = new Dictionary<string, IReadOnlyDictionary<NotificationChannel, bool>>
        {
            ["billing"] = new Dictionary<NotificationChannel, bool> { [NotificationChannel.Email] = false, [NotificationChannel.Sms] = true },
        };
        Assert.Equal([NotificationChannel.InApp, NotificationChannel.Sms], o.EffectiveChannels("billing", overrides));
        Assert.Equal([NotificationChannel.InApp], o.EffectiveChannels("other", overrides));
    }

    [Fact]
    public async Task Inbox_mark_read_is_scoped_to_owner()
    {
        var store = new InMemoryInboxStore();
        var item = new InboxItem(Guid.NewGuid(), "u-1", "c", "t", "b", DateTimeOffset.UnixEpoch, null);
        Assert.True(await store.TryAddAsync(item));
        Assert.False(await store.TryAddAsync(item));
        Assert.False(await store.MarkReadAsync("u-2", item.Id, DateTimeOffset.UnixEpoch));
        Assert.Equal(1, await store.UnreadCountAsync("u-1"));
        Assert.True(await store.MarkReadAsync("u-1", item.Id, DateTimeOffset.UnixEpoch));
        Assert.Equal(0, await store.UnreadCountAsync("u-1"));
    }
}

public class NotificationEndpointTests
{
    private static async Task<KestrelTestHost> StartAsync() =>
        await KestrelTestHost.StartAsync(
            b =>
            {
                TestAuthHandler.Register(b.Services);
                b.Services.AddBlocksWeb().AddBlocksOutbox(o => { o.RunDispatcher = false; o.JitterFactor = 0; })
                    .AddBlocksNotifications(o => o.AddTemplate("hello", "Hello", "Hi {{name}}"));
            },
            app =>
            {
                app.UseBlocksWeb();
                app.UseBlocksOutbox();
                app.MapBlocksNotifications();
                app.MapPost("/send", async (INotificationService n) =>
                    (await n.SendAsync("u-1", "social", "hello", new Dictionary<string, string> { ["name"] = "Ada" })).ToHttpResult());
            });

    private static Dictionary<string, string> As(string user) => new() { ["X-Test-User"] = user };

    [Fact]
    public async Task Inbox_flow_send_list_count_mark_read()
    {
        await using var host = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Post, "/send")).StatusCode);
        var processor = host.App.Services.GetRequiredService<OutboxProcessor>();
        await processor.ProcessDueAsync();
        await processor.ProcessDueAsync();

        var count = JsonDocument.Parse(await (await host.SendAsync(HttpMethod.Get, "/notifications/inbox/unread-count", headers: As("u-1"))).Content.ReadAsStringAsync());
        Assert.Equal(1, count.RootElement.GetProperty("unread").GetInt32());
        var inbox = JsonDocument.Parse(await (await host.SendAsync(HttpMethod.Get, "/notifications/inbox", headers: As("u-1"))).Content.ReadAsStringAsync()).RootElement;
        var item = inbox.GetProperty("items")[0];
        Assert.Equal("Hi Ada", item.GetProperty("body").GetString());
        var id = item.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, $"/notifications/inbox/{id}/read", headers: As("u-2"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync(HttpMethod.Post, $"/notifications/inbox/{id}/read", headers: As("u-1"))).StatusCode);
        var after = JsonDocument.Parse(await (await host.SendAsync(HttpMethod.Get, "/notifications/inbox/unread-count", headers: As("u-1"))).Content.ReadAsStringAsync());
        Assert.Equal(0, after.RootElement.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task Endpoints_require_authentication()
    {
        await using var host = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/notifications/inbox")).StatusCode);
    }

    [Fact]
    public async Task Preferences_can_be_set_and_read()
    {
        await using var host = await StartAsync();
        var put = await host.SendAsync(HttpMethod.Put, "/notifications/preferences", new { category = "billing", channel = 1, enabled = false }, headers: As("u-1"));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var body = await (await host.SendAsync(HttpMethod.Get, "/notifications/preferences", headers: As("u-1"))).Content.ReadAsStringAsync();
        Assert.Contains("billing", body);
        var bad = await host.SendAsync(HttpMethod.Put, "/notifications/preferences", new { category = "", channel = 1, enabled = false }, headers: As("u-1"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Hub_negotiate_requires_auth_and_succeeds_when_signed_in()
    {
        await using var host = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Post, "/notifications/hub/negotiate?negotiateVersion=1")).StatusCode);
        var ok = await host.SendAsync(HttpMethod.Post, "/notifications/hub/negotiate?negotiateVersion=1", headers: As("u-1"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("connectionToken", await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Failed_request_discards_staged_notifications()
    {
        await using var host = await KestrelTestHost.StartAsync(
            b => { TestAuthHandler.Register(b.Services); b.Services.AddBlocksWeb().AddBlocksOutbox(o => o.RunDispatcher = false).AddBlocksNotifications(o => o.AddTemplate("hello", "Hello", "Hi")); },
            app =>
            {
                app.UseBlocksWeb();
                app.UseBlocksOutbox();
                app.MapPost("/send-then-fail", async (INotificationService n) =>
                {
                    await n.SendAsync("u-1", "social", "hello");
                    return Error.Conflict("x.conflict", "Nope.").ToProblem();
                });
            });
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Post, "/send-then-fail")).StatusCode);
        Assert.Empty(((InMemoryOutboxStore)host.App.Services.GetRequiredService<IOutboxStore>()).Snapshot());
    }
}
