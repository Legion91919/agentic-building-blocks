namespace Agentic.Blocks.Core;

/// <summary>The category of an <see cref="Error"/>. Web adapters map each category to an HTTP status code.</summary>
public enum ErrorType
{
    /// <summary>The input is invalid (HTTP 400).</summary>
    Validation,
    /// <summary>The caller is not authenticated (HTTP 401).</summary>
    Unauthorized,
    /// <summary>A payment or an entitlement is needed (HTTP 402).</summary>
    PaymentRequired,
    /// <summary>The caller is authenticated but not allowed (HTTP 403).</summary>
    Forbidden,
    /// <summary>The resource does not exist (HTTP 404).</summary>
    NotFound,
    /// <summary>The request conflicts with the current state (HTTP 409).</summary>
    Conflict,
    /// <summary>An unexpected failure (HTTP 500). Prefer a more specific type.</summary>
    Failure,
}

/// <summary>
/// An expected, named failure. <see cref="Code"/> is stable and machine readable (for example
/// <c>auth.invalid_credentials</c>); <see cref="Message"/> is safe to show to an end user.
/// </summary>
public sealed record Error
{
    private static readonly IReadOnlyDictionary<string, string[]> NoFieldErrors = new Dictionary<string, string[]>();
    private static readonly IReadOnlyDictionary<string, object?> NoMetadata = new Dictionary<string, object?>();

    /// <summary>Creates an error.</summary>
    /// <exception cref="ArgumentException">When <paramref name="code"/> or <paramref name="message"/> is empty.</exception>
    public Error(string code, string message, ErrorType type,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
        Type = type;
        FieldErrors = fieldErrors ?? NoFieldErrors;
        Metadata = metadata ?? NoMetadata;
    }

    /// <summary>Stable, machine-readable code, for example <c>money.currency_mismatch</c>.</summary>
    public string Code { get; }

    /// <summary>Human-readable message without personal data.</summary>
    public string Message { get; }

    /// <summary>The error category.</summary>
    public ErrorType Type { get; }

    /// <summary>Per-field validation messages (empty unless <see cref="Type"/> is <see cref="ErrorType.Validation"/>).</summary>
    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }

    /// <summary>Extra, non-sensitive values a client may act on (for example a renew link).</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; }

    /// <summary>Returns a copy with one more metadata entry.</summary>
    public Error WithMetadata(string key, object? value)
    {
        var copy = new Dictionary<string, object?>(Metadata, StringComparer.Ordinal) { [key] = value };
        return new Error(Code, Message, Type, FieldErrors, copy);
    }

    /// <summary>Creates a validation error.</summary>
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        new(code, message, ErrorType.Validation, fieldErrors);

    /// <summary>Creates a validation error for one field.</summary>
    public static Error Validation(string code, string field, string message) =>
        new(code, message, ErrorType.Validation, new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>Creates a not-found error.</summary>
    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    /// <summary>Creates a conflict error.</summary>
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    /// <summary>Creates a forbidden error.</summary>
    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    /// <summary>Creates an unauthorized error.</summary>
    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    /// <summary>Creates a payment-required error.</summary>
    public static Error PaymentRequired(string code, string message) => new(code, message, ErrorType.PaymentRequired);

    /// <summary>Creates an unexpected-failure error.</summary>
    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);

    /// <inheritdoc />
    public override string ToString() => $"{Type}: {Code}: {Message}";
}
