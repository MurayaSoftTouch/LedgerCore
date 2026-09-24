using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LedgerCore.Ledger.Domain;

namespace LedgerCore.Ledger.Api.Application;

/// <summary>Format of an <c>Idempotency-Key</c>, shared by the API and a database check constraint.</summary>
internal static class IdempotencyKeyRules
{
    public const int MinimumLength = 8;

    public const int MaximumLength = 128;

    /// <summary>Valid as both a .NET regex and a PostgreSQL POSIX regex. A UUID fits.</summary>
    public const string SqlPattern = "^[A-Za-z0-9._:~-]{8,128}$";
}

/// <summary>A client-chosen key that makes a retried command safe (ADR-015). Opaque to the ledger.</summary>
internal sealed partial record IdempotencyKey
{
    public const string Header = "Idempotency-Key";

    private IdempotencyKey(string value) => Value = value;

    public string Value { get; }

    /// <summary>
    /// What logs show instead of the key: the first 12 hex digits of its SHA-256. Enough to match a
    /// log line to a claim, and it reveals nothing if a client put something sensitive in the key.
    /// </summary>
    public string Reference => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Value)))[..12];

    /// <summary>Validates a header value: required, 8–128 characters from <c>A–Z a–z 0–9 . _ : ~ -</c>.</summary>
    public static IdempotencyKey Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new LedgerDomainException(
                DomainErrorKind.Invalid, "IDEMPOTENCY_KEY_REQUIRED", $"The {Header} header is required for this command.");
        }

        if (!Pattern().IsMatch(value))
        {
            throw new LedgerDomainException(
                DomainErrorKind.Invalid,
                "IDEMPOTENCY_KEY_INVALID",
                $"{Header} must be {IdempotencyKeyRules.MinimumLength}–{IdempotencyKeyRules.MaximumLength} characters from A–Z, a–z, 0–9 and . _ : ~ -; a UUID is recommended.");
        }

        return new IdempotencyKey(value);
    }

    public override string ToString() => Value;

    [GeneratedRegex(IdempotencyKeyRules.SqlPattern, RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
