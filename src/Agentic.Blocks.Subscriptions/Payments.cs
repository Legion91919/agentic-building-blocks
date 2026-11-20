using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentic.Blocks.Core;

namespace Agentic.Blocks.Subscriptions;

/// <summary>Request to create a hosted checkout.</summary>
/// <param name="UserId">Paying user.</param>
/// <param name="Plan">Plan.</param>
/// <param name="SuccessUrl">Where the provider sends the user after paying.</param>
/// <param name="CancelUrl">Where the provider sends the user after cancelling.</param>
public sealed record CheckoutRequest(string UserId, Plan Plan, string SuccessUrl, string CancelUrl);

/// <summary>A hosted checkout page.</summary>
/// <param name="SessionId">Provider session id.</param>
/// <param name="Url">URL to redirect the user to.</param>
/// <param name="ExpiresAt">When the session expires.</param>
public sealed record HostedCheckout(string SessionId, Uri Url, DateTimeOffset ExpiresAt);

/// <summary>Provider-neutral payment event types.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PaymentEventType>))]
public enum PaymentEventType
{
    /// <summary>The user finished the hosted checkout.</summary>
    CheckoutCompleted,
    /// <summary>A (renewal) payment was collected.</summary>
    PaymentSucceeded,
    /// <summary>A payment failed.</summary>
    PaymentFailed,
    /// <summary>The subscription was cancelled at the provider.</summary>
    SubscriptionCancelled,
}

/// <summary>A verified, parsed webhook event.</summary>
/// <param name="EventId">Provider event id (used for idempotency).</param>
/// <param name="Type">Event type.</param>
/// <param name="CheckoutSessionId">Session id for checkout events.</param>
/// <param name="SubscriptionId">Our subscription id for other events.</param>
/// <param name="OccurredAt">When it happened at the provider.</param>
public sealed record PaymentEvent(string EventId, PaymentEventType Type, string? CheckoutSessionId, string? SubscriptionId, DateTimeOffset OccurredAt);

/// <summary>Payment-provider port. One adapter per provider; app code never sees provider types.</summary>
public interface IPaymentProvider
{
    /// <summary>Provider name used in the webhook route, for example <c>fake</c>.</summary>
    string Name { get; }

    /// <summary>Creates a hosted checkout session.</summary>
    Task<HostedCheckout> CreateCheckoutSessionAsync(CheckoutRequest request, CancellationToken cancellationToken = default);

    /// <summary>Verifies the signature and parses a webhook. Fails with a validation or unauthorized error.</summary>
    Result<PaymentEvent> ParseWebhook(string body, IReadOnlyDictionary<string, string> headers);
}

/// <summary>Options for <see cref="FakePaymentProvider"/>.</summary>
public sealed class FakePaymentOptions
{
    /// <summary>Base URL of the pretend hosted checkout page.</summary>
    public Uri CheckoutBaseUrl { get; set; } = new("https://pay.example.com/checkout/");

    /// <summary>Signing secret. Leave empty to use a random per-process secret (recommended; never commit one).</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Allowed clock skew for signed webhooks. Default 5 minutes.</summary>
    public TimeSpan Tolerance { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// A fake provider for development and tests. It returns hosted-checkout URLs on a reserved domain and signs webhook
/// events with HMAC-SHA256 like real providers do (header <c>Fake-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;</c>).
/// </summary>
public sealed class FakePaymentProvider : IPaymentProvider
{
    /// <summary>Signature header name.</summary>
    public const string SignatureHeader = "Fake-Signature";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly byte[] _secret;
    private readonly FakePaymentOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the provider.</summary>
    public FakePaymentProvider(FakePaymentOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _time = time;
        _secret = string.IsNullOrEmpty(options.WebhookSecret) ? RandomNumberGenerator.GetBytes(32) : Encoding.UTF8.GetBytes(options.WebhookSecret);
    }

    /// <inheritdoc />
    public string Name => "fake";

    /// <inheritdoc />
    public Task<HostedCheckout> CreateCheckoutSessionAsync(CheckoutRequest request, CancellationToken cancellationToken = default)
    {
        var id = "cs_" + Guid.NewGuid().ToString("N");
        return Task.FromResult(new HostedCheckout(id, new Uri(_options.CheckoutBaseUrl, id), _time.GetUtcNow().AddHours(1)));
    }

    /// <summary>Builds a signed webhook, as the provider would send it.</summary>
    public (string Body, IReadOnlyDictionary<string, string> Headers) CreateSignedEvent(PaymentEventType type, string? checkoutSessionId = null, string? subscriptionId = null, string? eventId = null)
    {
        var evt = new PaymentEvent(eventId ?? "evt_" + Guid.NewGuid().ToString("N"), type, checkoutSessionId, subscriptionId, _time.GetUtcNow());
        var body = JsonSerializer.Serialize(evt, Json);
        var t = _time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return (body, new Dictionary<string, string> { [SignatureHeader] = $"t={t},v1={Sign(t, body)}" });
    }

    /// <inheritdoc />
    public Result<PaymentEvent> ParseWebhook(string body, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var header = headers.FirstOrDefault(h => string.Equals(h.Key, SignatureHeader, StringComparison.OrdinalIgnoreCase)).Value;
        var parts = (header ?? string.Empty).Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        if (!parts.TryGetValue("t", out var t) || !parts.TryGetValue("v1", out var v1) || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
        {
            return Error.Unauthorized("subscriptions.webhook_signature", "The webhook signature is missing or malformed.");
        }

        if ((_time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(unix)).Duration() > _options.Tolerance)
        {
            return Error.Unauthorized("subscriptions.webhook_signature", "The webhook timestamp is outside the allowed window.");
        }

        var expected = Encoding.ASCII.GetBytes(Sign(t, body ?? string.Empty));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(v1)))
        {
            return Error.Unauthorized("subscriptions.webhook_signature", "The webhook signature does not match.");
        }

        try
        {
            var evt = JsonSerializer.Deserialize<PaymentEvent>(body!, Json);
            return evt is null || string.IsNullOrEmpty(evt.EventId)
                ? Error.Validation("subscriptions.webhook_invalid", "The webhook body is not a payment event.")
                : evt;
        }
        catch (JsonException)
        {
            return Error.Validation("subscriptions.webhook_invalid", "The webhook body is not valid JSON.");
        }
    }

    private string Sign(string timestamp, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(timestamp + "." + body))).ToUpperInvariant();
}
