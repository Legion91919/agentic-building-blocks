using System.Diagnostics.CodeAnalysis;

namespace Agentic.Blocks.Core;

/// <summary>The outcome of an operation that has no value: success, or an <see cref="Core.Error"/>.</summary>
public class Result
{
    private readonly Error? _error;

    /// <summary>Creates a result. Use <see cref="Success()"/> or <see cref="Failure(Core.Error)"/>.</summary>
    protected Result(Error? error) => _error = error;

    /// <summary>True when the operation succeeded.</summary>
    [MemberNotNullWhen(false, nameof(ErrorOrNull))]
    public bool IsSuccess => _error is null;

    /// <summary>True when the operation failed.</summary>
    [MemberNotNullWhen(true, nameof(ErrorOrNull))]
    public bool IsFailure => _error is not null;

    /// <summary>The error, or null on success.</summary>
    public Error? ErrorOrNull => _error;

    /// <summary>The error.</summary>
    /// <exception cref="InvalidOperationException">When the result is a success.</exception>
    public Error Error => _error ?? throw new InvalidOperationException("A successful result has no error.");

    private static readonly Result SuccessInstance = new(null);

    /// <summary>A successful result.</summary>
    public static Result Success() => SuccessInstance;

    /// <summary>A failed result.</summary>
    public static Result Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>A successful result with a value.</summary>
    public static Result<T> Success<T>(T value) => new(value);

    /// <summary>A failed result for a value of type <typeparamref name="T"/>.</summary>
    public static Result<T> Failure<T>(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>Converts an error into a failed result.</summary>
    public static implicit operator Result(Error error) => Failure(error);

    /// <summary>Named alternative to the implicit conversion.</summary>
    public static Result FromError(Error error) => Failure(error);

    /// <summary>Runs one of two functions depending on the outcome.</summary>
    public TOut Match<TOut>(Func<TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess() : onFailure(Error);
    }

    /// <inheritdoc />
    public override string ToString() => IsSuccess ? "Success" : $"Failure({Error})";
}

/// <summary>The outcome of an operation that returns a <typeparamref name="T"/> on success.</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T value) : base(null) => _value = value;

    internal Result(Error error) : base(error) => _value = default;

    /// <summary>The value.</summary>
    /// <exception cref="InvalidOperationException">When the result is a failure.</exception>
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"A failed result has no value: {Error}");

    /// <summary>Converts a value into a successful result.</summary>
    public static implicit operator Result<T>(T value) => new(value);

    /// <summary>Converts an error into a failed result.</summary>
    public static implicit operator Result<T>(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>Transforms the value when successful.</summary>
    public Result<TOut> Map<TOut>(Func<T, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? new Result<TOut>(map(_value!)) : new Result<TOut>(Error);
    }

    /// <summary>Chains another operation that can fail.</summary>
    public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return IsSuccess ? next(_value!) : new Result<TOut>(Error);
    }

    /// <summary>Runs one of two functions depending on the outcome.</summary>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(_value!) : onFailure(Error);
    }

    /// <inheritdoc />
    public override string ToString() => IsSuccess ? $"Success({_value})" : $"Failure({Error})";
}
