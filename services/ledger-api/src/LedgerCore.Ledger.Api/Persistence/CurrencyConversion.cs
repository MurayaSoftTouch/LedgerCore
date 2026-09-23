using LedgerCore.Ledger.Domain.Monetary;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LedgerCore.Ledger.Api.Persistence;

internal static class CurrencyConversion
{
    public static readonly ValueConverter<Currency, string> Converter = new(c => c.Code, code => Currency.FromCode(code));
}
