using LedgerCore.Ledger.Api.Persistence;

namespace LedgerCore.Ledger.Api.Application;

/// <summary>
/// <b>Not a production approval workflow.</b> Milestone 1 has no policy engine, so tests use this to
/// move a journal from PENDING_APPROVAL to APPROVED or REJECTED. It is internal, not registered in
/// dependency injection, and not reachable over HTTP. Milestone 2/3 replaces it with a recorded
/// policy decision (ADR-005, ADR-006).
/// </summary>
internal sealed class TestApprovalRecorder(LedgerDbContext db, TimeProvider time)
{
    private readonly JournalCommands _journals = new(db, time);

    public Task ApproveAsync(Guid ledgerId, Guid journalId, string approver, CancellationToken ct = default) =>
        _journals.InTransactionAsync(ledgerId, journalId, journal =>
        {
            journal.Approve(approver, time.GetUtcNow());
            return Task.CompletedTask;
        }, ct);

    public Task RejectAsync(Guid ledgerId, Guid journalId, string rejecter, string reason, CancellationToken ct = default) =>
        _journals.InTransactionAsync(ledgerId, journalId, journal =>
        {
            journal.Reject(rejecter, reason, time.GetUtcNow());
            return Task.CompletedTask;
        }, ct);
}
