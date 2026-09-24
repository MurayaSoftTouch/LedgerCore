using LedgerCore.Ledger.Domain.Journals;

namespace LedgerCore.Ledger.Api.Persistence;

/// <summary>
/// Append-only audit of journal status changes. Rows are inserted by the
/// <c>journals_audit_transition</c> trigger; the runtime role cannot insert, update or delete them.
/// </summary>
internal sealed class JournalStatusTransition
{
    public long Id { get; private set; }

    public Guid JournalId { get; private set; }

    public JournalStatus? FromStatus { get; private set; }

    public JournalStatus ToStatus { get; private set; }

    public string Actor { get; private set; } = null!;

    public DateTimeOffset OccurredAt { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>The recorded policy decision that authorized an APPROVED, REJECTED or POSTED transition.</summary>
    public Guid? PolicyDecisionId { get; private set; }

    /// <summary>The <c>Idempotency-Key</c> of the command that caused the transition, where there was one.</summary>
    public string? IdempotencyKey { get; private set; }
}
