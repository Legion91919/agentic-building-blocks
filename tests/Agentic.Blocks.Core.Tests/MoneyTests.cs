using Agentic.Blocks.Core;
using Xunit;

namespace Agentic.Blocks.Core.Tests;

public class MoneyTests
{
    [Theory]
    [InlineData("10.005", "EUR", "10.00")]
    [InlineData("10.015", "EUR", "10.02")]
    [InlineData("10.5", "JPY", "10")]
    [InlineData("11.5", "JPY", "12")]
    [InlineData("1.23456", "KWD", "1.235")]
    public void Rounds_to_minor_units_with_bankers_rounding(string amount, string currency, string expected)
    {
        var money = Money.Of(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), money.Amount);
    }

    [Fact]
    public void Away_from_zero_rounding_can_be_chosen()
    {
        Assert.Equal(10.01m, Money.Of(10.005m, "EUR", MidpointRounding.AwayFromZero).Amount);
    }

    [Fact]
    public void Currency_is_normalised_to_upper_case()
    {
        Assert.Equal("EUR", Money.Of(1m, "eur").Currency);
    }

    [Theory]
    [InlineData("EURO")]
    [InlineData("XYZ")]
    [InlineData("")]
    [InlineData("12A")]
    public void Unknown_currency_is_rejected(string currency)
    {
        Assert.Throws<ArgumentException>(() => Money.Of(1m, currency));
        Assert.Equal("money.unknown_currency", Money.TryOf(1m, currency).Error.Code);
    }

    [Fact]
    public void Adds_and_subtracts_same_currency()
    {
        var a = Money.Of(10.10m, "USD");
        var b = Money.Of(0.95m, "USD");
        Assert.Equal(Money.Of(11.05m, "USD"), a + b);
        Assert.Equal(Money.Of(9.15m, "USD"), a - b);
    }

    [Fact]
    public void Currency_mismatch_throws_on_add()
    {
        var ex = Assert.Throws<CurrencyMismatchException>(() => _ = Money.Of(1m, "EUR") + Money.Of(1m, "USD"));
        Assert.Equal("EUR", ex.Left);
        Assert.Equal("USD", ex.Right);
    }

    [Fact]
    public void Currency_mismatch_throws_on_subtract_and_compare()
    {
        Assert.Throws<CurrencyMismatchException>(() => _ = Money.Of(1m, "EUR") - Money.Of(1m, "GBP"));
        Assert.Throws<CurrencyMismatchException>(() => _ = Money.Of(1m, "EUR") < Money.Of(1m, "GBP"));
    }

    [Fact]
    public void Equal_currency_and_amount_are_equal_regardless_of_scale()
    {
        Assert.Equal(Money.Of(1.0m, "EUR"), Money.Of(1.00m, "EUR"));
        Assert.Equal(Money.Of(1.0m, "EUR").GetHashCode(), Money.Of(1.00m, "EUR").GetHashCode());
        Assert.False(Money.Of(1m, "EUR") == Money.Of(1m, "USD"));
    }

    [Fact]
    public void Multiply_rounds_result()
    {
        Assert.Equal(Money.Of(3.33m, "EUR"), Money.Of(9.99m, "EUR") * (1m / 3m));
    }

    [Fact]
    public void Allocate_preserves_total()
    {
        var parts = Money.Of(10m, "EUR").Allocate(3);
        Assert.Equal([Money.Of(3.34m, "EUR"), Money.Of(3.33m, "EUR"), Money.Of(3.33m, "EUR")], parts);
        Assert.Equal(Money.Of(10m, "EUR"), parts.Aggregate((x, y) => x + y));
    }

    [Fact]
    public void Allocate_negative_amount_preserves_total()
    {
        var parts = Money.Of(-0.05m, "EUR").Allocate(2);
        Assert.Equal(Money.Of(-0.05m, "EUR"), parts[0] + parts[1]);
    }

    [Fact]
    public void Allocate_rejects_zero_parts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Of(1m, "EUR").Allocate(0));
    }

    [Fact]
    public void Minor_units_round_trip()
    {
        Assert.Equal(1999L, Money.Of(19.99m, "EUR").ToMinorUnits());
        Assert.Equal(Money.Of(19.99m, "EUR"), Money.FromMinorUnits(1999, "EUR"));
        Assert.Equal(Money.Of(500m, "JPY"), Money.FromMinorUnits(500, "JPY"));
    }

    [Fact]
    public void Formats_with_minor_units_invariantly()
    {
        Assert.Equal("12.30 EUR", Money.Of(12.3m, "EUR").ToString());
        Assert.Equal("1200 JPY", Money.Of(1200m, "JPY").ToString());
        Assert.Equal("0.500 KWD", Money.Of(0.5m, "KWD").ToString());
    }

    [Fact]
    public void Comparison_operators_work()
    {
        Assert.True(Money.Of(2m, "EUR") > Money.Of(1m, "EUR"));
        Assert.True(Money.Of(1m, "EUR") <= Money.Of(1m, "EUR"));
        Assert.True(Money.Zero("EUR").IsZero);
        Assert.True((-Money.Of(1m, "EUR")).IsNegative);
    }
}
