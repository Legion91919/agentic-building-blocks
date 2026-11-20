namespace Agentic.Blocks.Core;

/// <summary>ISO 4217 currency codes and their minor units.</summary>
public static class Currencies
{
    private static readonly HashSet<string> Known = new(
        ("AED AFN ALL AMD ANG AOA ARS AUD AWG AZN BAM BBD BDT BGN BHD BIF BMD BND BOB BRL BSD BTN BWP BYN BZD CAD CDF CHF " +
         "CLP CNY COP CRC CUP CVE CZK DJF DKK DOP DZD EGP ERN ETB EUR FJD FKP GBP GEL GHS GIP GMD GNF GTQ GYD HKD HNL HTG " +
         "HUF IDR ILS INR IQD IRR ISK JMD JOD JPY KES KGS KHR KMF KPW KRW KWD KYD KZT LAK LBP LKR LRD LSL LYD MAD MDL MGA " +
         "MKD MMK MNT MOP MRU MUR MVR MWK MXN MYR MZN NAD NGN NIO NOK NPR NZD OMR PAB PEN PGK PHP PKR PLN PYG QAR RON RSD " +
         "RUB RWF SAR SBD SCR SDG SEK SGD SHP SLE SOS SRD SSP STN SVC SYP SZL THB TJS TMT TND TOP TRY TTD TWD TZS UAH UGX " +
         "USD UYU UZS VES VND VUV WST XAF XCD XCG XOF XPF YER ZAR ZMW ZWG").Split(' '),
        StringComparer.Ordinal);

    private static readonly HashSet<string> ZeroDecimals = new(
        "BIF CLP DJF GNF ISK JPY KMF KRW PYG RWF UGX VND VUV XAF XOF XPF".Split(' '), StringComparer.Ordinal);

    private static readonly HashSet<string> ThreeDecimals = new(
        "BHD IQD JOD KWD LYD OMR TND".Split(' '), StringComparer.Ordinal);

    /// <summary>True when <paramref name="code"/> (any case) is a known ISO 4217 code.</summary>
    public static bool IsKnown(string? code) =>
        code is { Length: 3 } && Known.Contains(code.ToUpperInvariant());

    /// <summary>Upper-cases and validates a code.</summary>
    /// <exception cref="ArgumentException">When the code is unknown.</exception>
    public static string Normalize(string currency)
    {
        if (!IsKnown(currency))
        {
            throw new ArgumentException($"'{currency}' is not an ISO 4217 currency code.", nameof(currency));
        }

        return currency.ToUpperInvariant();
    }

    /// <summary>Number of decimals for a code (2 unless the currency uses 0 or 3).</summary>
    public static int MinorUnits(string code) =>
        ZeroDecimals.Contains(code) ? 0 : ThreeDecimals.Contains(code) ? 3 : 2;
}
