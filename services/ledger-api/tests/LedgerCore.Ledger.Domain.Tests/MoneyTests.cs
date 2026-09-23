using LedgerCore.Ledger.Domain.Monetary;

namespace LedgerCore.Ledger.Domain.Tests;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("KES", "0.01")]
    [InlineData("KES", "125000.50")]
    [InlineData("UGX", "1")]
    [InlineData("BHD", "0.001")]
    [InlineData("KES", "999999999999999999.99")]
    public void AcceptsAmountsWithinCurrencyPrecision(string currency, string amount)
    {
        var value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);

        var money = Money.Of(value, Currency.FromCode(currency));

        Assert.Equal(value, money.Amount);
    }

    [Fact]
    public void TrailingZerosDoNotCountAsExtraPrecision()
    {
        Assert.Equal(1.5m, Money.Of(1.5000m, Currency.FromCode("KES")).Amount);
    }

    [Theory]
    [InlineData("KES", "0.001")]
    [InlineData("UGX", "1.5")]
    [InlineData("JPY", "0.1")]
    [InlineData("BHD", "0.0001")]
    public void RejectsExcessPrecisionInsteadOfRounding(string currency, string amount)
    {
        var value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);

        DomainAssert.Fails("AMOUNT_PRECISION_EXCEEDED", () => Money.Of(value, Currency.FromCode(currency)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("-100")]
    public void RejectsZeroAndNegativeAmounts(string amount)
    {
        var value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);

        DomainAssert.Fails("AMOUNT_NOT_POSITIVE", () => Money.Of(value, Currency.FromCode("KES")));
    }

    [Fact]
    public void RejectsAmountsAboveStorageRange()
    {
        DomainAssert.Fails("AMOUNT_OUT_OF_RANGE", () => Money.Of(Money.MaxValue + 1m, Currency.FromCode("KES")));
    }

    [Theory]
    [InlineData("kes")]
    [InlineData("XYZ")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsUnsupportedCurrencies(string? code)
    {
        DomainAssert.Fails("CURRENCY_UNSUPPORTED", () => Currency.FromCode(code));
    }
}
