using Agentic.Blocks.Core;
using Xunit;

namespace Agentic.Blocks.Core.Tests;

public class ResultTests
{
    [Fact]
    public void Success_has_no_error()
    {
        var result = Result.Success();
        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.ErrorOrNull);
        Assert.Throws<InvalidOperationException>(() => _ = result.Error);
    }

    [Fact]
    public void Failure_exposes_error()
    {
        Result result = Error.NotFound("thing.not_found", "No such thing.");
        Assert.True(result.IsFailure);
        Assert.Equal("thing.not_found", result.Error.Code);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
    }

    [Fact]
    public void Value_of_failed_result_throws()
    {
        Result<int> result = Error.Conflict("x.conflict", "Conflict.");
        Assert.Throws<InvalidOperationException>(() => _ = result.Value);
    }

    [Fact]
    public void Implicit_value_conversion_succeeds()
    {
        Result<string> result = "hello";
        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Value);
    }

    [Fact]
    public void Map_and_bind_short_circuit_on_failure()
    {
        Result<int> failed = Error.Validation("n.bad", "n", "Bad number.");
        var calls = 0;
        var mapped = failed.Map(x => { calls++; return x * 2; }).Bind(x => Result.Success(x + 1));
        Assert.True(mapped.IsFailure);
        Assert.Equal("n.bad", mapped.Error.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Map_and_bind_chain_on_success()
    {
        var result = Result.Success(20).Map(x => x * 2).Bind(x => x > 10 ? Result.Success(x + 2) : Result.Failure<int>(Error.Failure("f", "f")));
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Match_picks_branch()
    {
        Assert.Equal("ok:3", Result.Success(3).Match(v => $"ok:{v}", e => e.Code));
        Assert.Equal("e.code", Result.Failure<int>(Error.Forbidden("e.code", "No.")).Match(v => "ok", e => e.Code));
        Assert.Equal("yes", Result.Success().Match(() => "yes", _ => "no"));
    }

    [Fact]
    public void Failure_rejects_null_error()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
    }
}

public class ErrorTests
{
    [Theory]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.PaymentRequired)]
    [InlineData(ErrorType.Failure)]
    public void Factories_set_type(ErrorType type)
    {
        var error = type switch
        {
            ErrorType.Validation => Error.Validation("c", "m"),
            ErrorType.NotFound => Error.NotFound("c", "m"),
            ErrorType.Conflict => Error.Conflict("c", "m"),
            ErrorType.Forbidden => Error.Forbidden("c", "m"),
            ErrorType.Unauthorized => Error.Unauthorized("c", "m"),
            ErrorType.PaymentRequired => Error.PaymentRequired("c", "m"),
            _ => Error.Failure("c", "m"),
        };
        Assert.Equal(type, error.Type);
    }

    [Fact]
    public void Empty_code_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => _ = new Error(" ", "m", ErrorType.Failure));
    }

    [Fact]
    public void Field_validation_error_carries_field()
    {
        var error = Error.Validation("email.invalid", "email", "Enter a valid email.");
        Assert.Equal("Enter a valid email.", Assert.Single(error.FieldErrors["email"]));
    }

    [Fact]
    public void WithMetadata_returns_copy()
    {
        var original = Error.PaymentRequired("sub.required", "Upgrade.");
        var copy = original.WithMetadata("renewUrl", "/plans");
        Assert.Empty(original.Metadata);
        Assert.Equal("/plans", copy.Metadata["renewUrl"]);
    }
}
