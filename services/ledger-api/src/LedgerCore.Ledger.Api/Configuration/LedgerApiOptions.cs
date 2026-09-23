using System.ComponentModel.DataAnnotations;

namespace LedgerCore.Ledger.Api.Configuration;

/// <summary>
/// Startup-validated configuration. The host refuses to start when any constraint fails,
/// so a misconfigured ledger never accepts financial requests.
/// </summary>
internal sealed class LedgerApiOptions
{
    public const string SectionName = "Ledger";

    /// <summary>Ledger-policy contract version this service speaks (contracts/openapi/policy-decision.v1.yaml).</summary>
    [Required]
    [RegularExpression(@"^\d+\.\d+\.\d+$")]
    public string ContractVersion { get; init; } = string.Empty;

    /// <summary>Base URL of the policy service. Not called yet; validated now so misconfiguration surfaces early.</summary>
    [Required]
    [Url]
    public string PolicyServiceBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Upper bound on a policy decision call. On expiry the ledger fails closed (ADR-005).
    /// </summary>
    [Range(100, 30_000)]
    public int PolicyDecisionTimeoutMs { get; init; } = 2_000;
}
