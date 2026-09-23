using System.Text.RegularExpressions;

namespace LedgerCore.Ledger.Api.Integration.Correlation;

/// <summary>
/// The correlation id of the current operation (ADR-012). Flows with the async context from the
/// inbound request to outbound policy calls. It is a tracing aid, never an authentication token.
/// </summary>
internal static partial class CorrelationId
{
    public const string Header = "X-Correlation-Id";

    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string? Current
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = value;
    }

    /// <summary>Returns the supplied value if it is safe to propagate and log, otherwise a new id.</summary>
    public static string AcceptOrCreate(string? supplied) =>
        supplied is not null && Accepted().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString();

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex Accepted();
}
