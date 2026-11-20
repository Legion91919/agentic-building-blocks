using System.Globalization;

namespace Agentic.Blocks.Core;

/// <summary>
/// An amount of money in one ISO 4217 currency. Amounts are <see cref="decimal"/> and are always rounded to the
/// currency's minor unit (2 for EUR, 0 for JPY, 3 for KWD). Arithmetic is only allowed within one currency.
/// </summary>
public sealed record Money : IComparable<Money>
{
    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    /// <summary>The amount, rounded to <see cref="MinorUnits"/> decimals.</summary>
    public decimal Amount { get; }

    /// <summary>The upper-case ISO 4217 code, for example <c>EUR</c>.</summary>
    public string Currency { get; }

    /// <summary>Number of decimals of the currency.</summary>
    public int MinorUnits => Currencies.MinorUnits(Currency);

    /// <summary>
    /// Creates money, rounding to the currency's minor unit with banker's rounding (<see cref="MidpointRounding.ToEven"/>)
    /// unless another <paramref name="rounding"/> is given.
    /// </summary>
    /// <exception cref="ArgumentException">When the currency is not a known ISO 4217 code.</exception>
    public static Money Of(decimal amount, string currency, MidpointRounding rounding = MidpointRounding.ToEven)
    {
        var code = Currencies.Normalize(currency);
        return new Money(Math.Round(amount, Currencies.MinorUnits(code), rounding), code);
    }

    /// <summary>Tries to create money; returns a validation error for an unknown currency.</summary>
    public static Result<Money> TryOf(decimal amount, string? currency, MidpointRounding rounding = MidpointRounding.ToEven) =>
        Currencies.IsKnown(currency)
            ? Of(amount, currency!, rounding)
            : Error.Validation("money.unknown_currency", "currency", $"'{currency}' is not an ISO 4217 currency code.");

    /// <summary>Creates money from minor units (cents), for example 1999 EUR cents is 19.99 EUR.</summary>
    public static Money FromMinorUnits(long minorUnits, string currency)
    {
        var code = Currencies.Normalize(currency);
        return new Money(minorUnits / Pow10(Currencies.MinorUnits(code)), code);
    }

    /// <summary>Zero in the given currency.</summary>
    public static Money Zero(string currency) => Of(0m, currency);

    /// <summary>The amount in minor units (cents).</summary>
    public long ToMinorUnits() => decimal.ToInt64(Amount * Pow10(MinorUnits));

    /// <summary>True when the amount is zero.</summary>
    public bool IsZero => Amount == 0m;

    /// <summary>True when the amount is below zero.</summary>
    public bool IsNegative => Amount < 0m;

    /// <summary>Adds two amounts of the same currency.</summary>
    /// <exception cref="CurrencyMismatchException">When the currencies differ.</exception>
    public Money Add(Money other) { EnsureSameCurrency(other); return new Money(Amount + other.Amount, Currency); }

    /// <summary>Subtracts an amount of the same currency.</summary>
    /// <exception cref="CurrencyMismatchException">When the currencies differ.</exception>
    public Money Subtract(Money other) { EnsureSameCurrency(other); return new Money(Amount - other.Amount, Currency); }

    /// <summary>Multiplies by a factor (for example a quantity or a tax rate), rounding to the minor unit.</summary>
    public Money Multiply(decimal factor, MidpointRounding rounding = MidpointRounding.ToEven) =>
        new(Math.Round(Amount * factor, MinorUnits, rounding), Currency);

    /// <summary>Returns the negated amount.</summary>
    public Money Negate() => new(-Amount, Currency);

    /// <summary>
    /// Splits the amount into <paramref name="parts"/> amounts that add up exactly to the original, spreading the
    /// leftover minor units over the first parts (10.00 / 3 = 3.34 + 3.33 + 3.33).
    /// </summary>
    public IReadOnlyList<Money> Allocate(int parts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parts, 1);
        var total = ToMinorUnits();
        var share = total / parts;
        var remainder = total % parts;
        var step = remainder >= 0 ? 1 : -1;
        var result = new Money[parts];
        for (var i = 0; i < parts; i++)
        {
            var extra = i < Math.Abs(remainder) ? step : 0;
            result[i] = FromMinorUnits(share + extra, Currency);
        }

        return result;
    }

    /// <inheritdoc />
    public int CompareTo(Money? other)
    {
        if (other is null) return 1;
        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    /// <summary>Adds two amounts of the same currency.</summary>
    public static Money operator +(Money left, Money right) => (left ?? throw new ArgumentNullException(nameof(left))).Add(right);

    /// <summary>Subtracts an amount of the same currency.</summary>
    public static Money operator -(Money left, Money right) => (left ?? throw new ArgumentNullException(nameof(left))).Subtract(right);

    /// <summary>Multiplies by a factor.</summary>
    public static Money operator *(Money left, decimal factor) => (left ?? throw new ArgumentNullException(nameof(left))).Multiply(factor);

    /// <summary>Negates the amount.</summary>
    public static Money operator -(Money value) => (value ?? throw new ArgumentNullException(nameof(value))).Negate();

    /// <summary>Compares two amounts of the same currency.</summary>
    public static bool operator <(Money left, Money right) => Compare(left, right) < 0;

    /// <summary>Compares two amounts of the same currency.</summary>
    public static bool operator >(Money left, Money right) => Compare(left, right) > 0;

    /// <summary>Compares two amounts of the same currency.</summary>
    public static bool operator <=(Money left, Money right) => Compare(left, right) <= 0;

    /// <summary>Compares two amounts of the same currency.</summary>
    public static bool operator >=(Money left, Money right) => Compare(left, right) >= 0;

    /// <summary>Formats as <c>12.30 EUR</c> using the invariant culture.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Amount.ToString("F" + MinorUnits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)} {Currency}");

    private static int Compare(Money left, Money right) => (left ?? throw new ArgumentNullException(nameof(left))).CompareTo(right);

    private void EnsureSameCurrency(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new CurrencyMismatchException(Currency, other.Currency);
        }
    }

    private static decimal Pow10(int n) => n switch { 0 => 1m, 1 => 10m, 2 => 100m, 3 => 1000m, _ => (decimal)Math.Pow(10, n) };
}

/// <summary>Thrown when money in two different currencies is combined.</summary>
public sealed class CurrencyMismatchException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public CurrencyMismatchException(string left, string right)
        : base($"Cannot combine {left} and {right}; convert explicitly first.")
    {
        Left = left;
        Right = right;
    }

    /// <summary>Creates the exception.</summary>
    public CurrencyMismatchException() : this("?", "?") { }

    /// <summary>Creates the exception.</summary>
    public CurrencyMismatchException(string message) : base(message) { Left = "?"; Right = "?"; }

    /// <summary>Creates the exception.</summary>
    public CurrencyMismatchException(string message, Exception innerException) : base(message, innerException) { Left = "?"; Right = "?"; }

    /// <summary>The left currency.</summary>
    public string Left { get; }

    /// <summary>The right currency.</summary>
    public string Right { get; }
}
