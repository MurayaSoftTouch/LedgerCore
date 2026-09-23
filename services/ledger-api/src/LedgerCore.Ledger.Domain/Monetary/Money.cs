namespace LedgerCore.Ledger.Domain.Monetary;

/// <summary>
/// A strictly positive, exact amount in one currency.
/// </summary>
/// <remarks>
/// Backed by <see cref="decimal"/> (base-10, exact for every value it accepts) and stored as
/// PostgreSQL <c>numeric(22,4)</c>. The ledger never rounds: an amount with more decimal places
/// than the currency allows is rejected, not adjusted.
/// </remarks>
public readonly record struct Money
{
    /// <summary>Largest amount a single entry or a journal total may carry: 18 integer digits, 4 decimals.</summary>
    public const decimal MaxValue = 999_999_999_999_999_999.9999m;

    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    public static Money Of(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);

        if (amount <= 0m)
        {
            throw new LedgerDomainException(
                DomainErrorKind.Invalid, "AMOUNT_NOT_POSITIVE", "Amounts must be greater than zero.");
        }

        if (amount > MaxValue)
        {
            throw new LedgerDomainException(
                DomainErrorKind.Invalid, "AMOUNT_OUT_OF_RANGE", $"Amounts must not exceed {MaxValue}.");
        }

        if (decimal.Round(amount, currency.MinorUnits, MidpointRounding.ToZero) != amount)
        {
            throw new LedgerDomainException(
                DomainErrorKind.Invalid,
                "AMOUNT_PRECISION_EXCEEDED",
                $"{currency.Code} amounts allow at most {currency.MinorUnits} decimal places.");
        }

        return new Money(amount, currency);
    }

    public override string ToString() => $"{Amount} {Currency.Code}";
}
