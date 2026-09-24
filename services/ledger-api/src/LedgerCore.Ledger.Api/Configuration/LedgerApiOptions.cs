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

    /// <summary>Base URL of the policy service.</summary>
    [Required]
    [Url]
    public string PolicyServiceBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Upper bound on a policy decision call. On expiry the ledger fails closed (ADR-005).
    /// </summary>
    [Range(100, 30_000)]
    public int PolicyDecisionTimeoutMs { get; init; } = 2_000;

    /// <summary>Upper bound on one attempt within the total budget.</summary>
    [Range(50, 30_000)]
    public int PolicyAttemptTimeoutMs { get; init; } = 800;

    /// <summary>Extra attempts after the first, for transient failures only (ADR-012).</summary>
    [Range(0, 5)]
    public int PolicyMaxRetries { get; init; } = 2;

    /// <summary>
    /// Shared service credential for the policy decision API (ADR-013). From the environment
    /// (<c>Ledger__PolicyServiceToken</c>) only; never logged.
    /// </summary>
    [Required]
    [MinLength(32)]
    public string PolicyServiceToken { get; init; } = string.Empty;

    /// <summary>
    /// Unpublished outbox events older than this are reported as <c>AGING</c> by <c>/ops/outbox</c>
    /// (Milestone 5). Between one second and seven days.
    /// </summary>
    [Range(1, 604_800)]
    public int OutboxAgingThresholdSeconds { get; init; } = 300;
}
