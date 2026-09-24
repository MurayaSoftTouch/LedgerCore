using System.Collections.Frozen;

namespace LedgerCore.Ledger.Domain.Monetary;

/// <summary>
/// An ISO 4217 currency the ledger supports, with its minor-unit exponent.
/// The list is closed on purpose: an unknown code is rejected rather than guessed at.
/// </summary>
public sealed record Currency
{
    private static readonly FrozenDictionary<string, int> Supported = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["KES"] = 2,
        ["UGX"] = 0,
        ["TZS"] = 2,
        ["RWF"] = 0,
        ["USD"] = 2,
        ["EUR"] = 2,
        ["GBP"] = 2,
        ["JPY"] = 0,
        ["BHD"] = 3,
        ["KWD"] = 3,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private Currency(string code, int minorUnits)
    {
        Code = code;
        MinorUnits = minorUnits;
    }

    public string Code { get; }

    /// <summary>Number of decimal places an amount in this currency may carry.</summary>
    public int MinorUnits { get; }

    public static IEnumerable<string> SupportedCodes => Supported.Keys;

    public static Currency FromCode(string? code)
    {
        if (code is null || !Supported.TryGetValue(code, out var minorUnits))
        {
            throw new LedgerDomainException(
                DomainErrorKind.Invalid, "CURRENCY_UNSUPPORTED", $"Currency '{code}' is not supported.");
        }

        return new Currency(code, minorUnits);
    }

    public override string ToString() => Code;
}
