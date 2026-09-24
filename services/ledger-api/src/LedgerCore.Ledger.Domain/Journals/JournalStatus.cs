namespace LedgerCore.Ledger.Domain.Journals;

/// <summary>
/// Persisted journal states (ADR-006). "Reversed" is deliberately absent: it is derived from the
/// existence of a posted reversal journal, so a posted row never changes.
/// </summary>
public enum JournalStatus
{
    Draft,
    PendingApproval,
    Approved,
    Rejected,
    Posted,
}
