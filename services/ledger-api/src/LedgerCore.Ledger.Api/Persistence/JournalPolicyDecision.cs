using LedgerCore.Ledger.Api.Integration.Policy;

namespace LedgerCore.Ledger.Api.Persistence;

/// <summary>
/// Evidence of the policy decision received for a journal (Milestone 3, ADR-012): enough to prove
/// which decision approved or rejected it, and nothing of the policy's rules. One row per journal
/// (the policy service returns the same decision for every retry of a transaction). Append-only:
/// database triggers refuse updates and deletes, and the lifecycle trigger requires matching
/// evidence for APPROVED and REJECTED.
/// </summary>
internal sealed class JournalPolicyDecision
{
    private JournalPolicyDecision()
    {
    }

    public Guid JournalId { get; private set; }

    public Guid DecisionId { get; private set; }

    public string PolicyVersion { get; private set; } = null!;

    public PolicyDecisionValue Decision { get; private set; }

    public List<string> ReasonCodes { get; private set; } = [];

    public DateTimeOffset EvaluatedAt { get; private set; }

    public string ContractVersion { get; private set; } = null!;

    public string? CorrelationId { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public static JournalPolicyDecision From(Guid journalId, PolicyDecision decision, string? correlationId, DateTimeOffset receivedAt) => new()
    {
        JournalId = journalId,
        DecisionId = decision.DecisionId,
        PolicyVersion = decision.PolicyVersion,
        Decision = decision.Decision,
        ReasonCodes = [.. decision.ReasonCodes],
        EvaluatedAt = decision.EvaluatedAt,
        ContractVersion = decision.ContractVersion,
        CorrelationId = correlationId,
        ReceivedAt = receivedAt,
    };
}
