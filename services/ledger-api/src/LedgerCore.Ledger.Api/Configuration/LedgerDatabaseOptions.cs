using System.ComponentModel.DataAnnotations;

namespace LedgerCore.Ledger.Api.Configuration;

/// <summary>Bound from <c>ConnectionStrings</c>; validated at startup.</summary>
internal sealed class LedgerDatabaseOptions
{
    public const string SectionName = "ConnectionStrings";

    /// <summary>
    /// Runtime connection, as the least-privilege <c>ledger_runtime</c> role (ADR-007). Never the schema owner.
    /// </summary>
    [Required]
    public string Ledger { get; init; } = string.Empty;
}
