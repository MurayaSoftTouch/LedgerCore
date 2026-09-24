using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LedgerCore.Ledger.Api.Persistence;

/// <summary>
/// Stores enums as SCREAMING_SNAKE_CASE text (<c>PENDING_APPROVAL</c>) so SQL, check constraints,
/// triggers and the JSON API all use the same spelling.
/// </summary>
internal static class EnumText
{
    public static string ToText<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.SnakeCaseUpper.ConvertName(value.ToString());

    public static T Parse<T>(string value)
        where T : struct, Enum => Enum.Parse<T>(value.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true);

    /// <summary>SQL list for a CHECK constraint, e.g. <c>'DEBIT', 'CREDIT'</c>.</summary>
    public static string SqlList<T>()
        where T : struct, Enum => string.Join(", ", Enum.GetValues<T>().Select(v => $"'{ToText(v)}'"));

    public static ValueConverter<T, string> Converter<T>()
        where T : struct, Enum => new(v => ToText(v), v => Parse<T>(v));
}
