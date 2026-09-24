namespace LedgerCore.Ledger.Domain;

internal static class Guard
{
    public static string Text(string? value, string field, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new LedgerDomainException(DomainErrorKind.Invalid, "FIELD_REQUIRED", $"{field} is required.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new LedgerDomainException(
                DomainErrorKind.Invalid, "FIELD_TOO_LONG", $"{field} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static string? OptionalText(string? value, string field, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Text(value, field, maxLength);

    /// <summary>Codes: 1–32 chars, letters, digits, '.', '_' or '-', starting with a letter or digit.</summary>
    public static string Code(string? value, string field)
    {
        var code = Text(value, field, 32);
        if (!char.IsAsciiLetterOrDigit(code[0]) || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new LedgerDomainException(
                DomainErrorKind.Invalid, "CODE_INVALID", $"{field} may contain only letters, digits, '.', '_' and '-'.");
        }

        return code;
    }

    public static string Actor(string? actor) => Text(actor, "actor", 128);

    public static T Defined<T>(T value, string field)
        where T : struct, Enum =>
        Enum.IsDefined(value)
            ? value
            : throw new LedgerDomainException(DomainErrorKind.Invalid, "ENUM_INVALID", $"{field} '{value}' is not recognised.");
}
