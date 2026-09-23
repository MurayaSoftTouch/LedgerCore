using LedgerCore.Ledger.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LedgerCore.Ledger.Api.Application;

/// <summary>
/// Explicit PostgreSQL row locks. Every command that changes a journal takes its row lock first and
/// only then reads it, so under READ COMMITTED the read sees the latest committed state.
/// </summary>
internal static class RowLocks
{
    public static Task LockJournalAsync(this LedgerDbContext db, Guid ledgerId, Guid journalId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM journals WHERE id = {journalId} AND ledger_id = {ledgerId} FOR UPDATE", ct);

    /// <summary>
    /// Shares-locks accounts (sorted, to avoid lock-order deadlocks) so they cannot be deactivated
    /// until the current transaction ends.
    /// </summary>
    public static Task LockAccountsForShareAsync(this LedgerDbContext db, Guid[] accountIds, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM accounts WHERE id = ANY({accountIds}) ORDER BY id FOR SHARE", ct);
}
