using LedgerCore.Ledger.Api.Persistence;
using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Journals;
using Microsoft.EntityFrameworkCore;
using DomainLedger = LedgerCore.Ledger.Domain.Ledgers.Ledger;

namespace LedgerCore.Ledger.Api.Application;

/// <param name="Journal">The journal as stored.</param>
/// <param name="ReversedByJournalId">
/// The posted reversal of this journal, if any. "Reversed" is derived from this, never stored (ADR-006).
/// </param>
internal sealed record JournalView(Journal Journal, Guid? ReversedByJournalId);

internal sealed class LedgerQueries(LedgerDbContext db)
{
    public async Task<DomainLedger> GetLedgerAsync(Guid ledgerId, CancellationToken ct) =>
        await db.Ledgers.AsNoTracking().SingleOrDefaultAsync(l => l.Id == ledgerId, ct) ?? throw NotFound.Ledger(ledgerId);

    public async Task<IReadOnlyList<Account>> ListAccountsAsync(Guid ledgerId, CancellationToken ct)
    {
        _ = await GetLedgerAsync(ledgerId, ct);
        return await db.Accounts.AsNoTracking().Where(a => a.LedgerId == ledgerId).OrderBy(a => a.Code).ToListAsync(ct);
    }

    public async Task<Account> GetAccountAsync(Guid ledgerId, Guid accountId, CancellationToken ct) =>
        await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.LedgerId == ledgerId, ct)
        ?? throw NotFound.Account(accountId);

    public async Task<JournalView> GetJournalAsync(Guid ledgerId, Guid journalId, CancellationToken ct)
    {
        var journal = await db.Journals.AsNoTracking().Include(j => j.Entries)
            .SingleOrDefaultAsync(j => j.Id == journalId && j.LedgerId == ledgerId, ct)
            ?? throw NotFound.Journal(journalId);

        var reversedBy = await db.Journals.AsNoTracking()
            .Where(j => j.ReversesJournalId == journalId && j.Status == JournalStatus.Posted)
            .Select(j => (Guid?)j.Id)
            .SingleOrDefaultAsync(ct);

        return new JournalView(journal, reversedBy);
    }
}
