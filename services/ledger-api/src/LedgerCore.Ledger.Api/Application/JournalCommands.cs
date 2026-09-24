using LedgerCore.Ledger.Api.Persistence;
using LedgerCore.Ledger.Domain;
using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Journals;
using LedgerCore.Ledger.Domain.Monetary;
using Microsoft.EntityFrameworkCore;

namespace LedgerCore.Ledger.Api.Application;

/// <summary>
/// Journal state changes. Each command runs in one database transaction that locks the journal row
/// before reading it; the domain validates, and the database triggers re-validate on write (ADR-007).
/// </summary>
internal sealed class JournalCommands(LedgerDbContext db, TimeProvider time)
{
    public async Task<Journal> CreateDraftAsync(
        Guid ledgerId, string currency, string description, string? externalReference, string actor, CancellationToken ct)
    {
        if (!await db.Ledgers.AnyAsync(l => l.Id == ledgerId, ct))
        {
            throw NotFound.Ledger(ledgerId);
        }

        var journal = Journal.CreateDraft(
            ledgerId, Currency.FromCode(currency), description, externalReference, actor, time.GetUtcNow());
        db.Journals.Add(journal);
        await db.SaveChangesAsync(ct);
        return journal;
    }

    public Task<Journal> AddEntryAsync(
        Guid ledgerId, Guid journalId, Guid accountId, EntryDirection direction, decimal amount, string? memo, CancellationToken ct) =>
        InTransactionAsync(ledgerId, journalId, async journal =>
        {
            var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId && a.LedgerId == ledgerId, ct)
                ?? throw new LedgerDomainException(
                    DomainErrorKind.RuleViolation, "ACCOUNT_NOT_FOUND", $"Account {accountId} does not exist in this ledger.");
            journal.AddEntry(account, direction, Money.Of(amount, journal.Currency), memo);
        }, ct);

    public Task<Journal> SubmitAsync(Guid ledgerId, Guid journalId, string actor, CancellationToken ct) =>
        InTransactionAsync(ledgerId, journalId, journal =>
        {
            journal.Submit(actor, time.GetUtcNow());
            return Task.CompletedTask;
        }, ct);

    /// <summary>
    /// Posts an approved journal. Inside one transaction: lock the journal, re-read its persisted
    /// entries, share-lock and re-read the accounts, validate, transition. Client totals are never used.
    /// </summary>
    public Task<Journal> PostAsync(Guid ledgerId, Guid journalId, string actor, CancellationToken ct) =>
        InTransactionAsync(ledgerId, journalId, async journal =>
        {
            var accounts = await LockAndLoadAccountsAsync(journal, ct);
            journal.Post(accounts, actor, time.GetUtcNow());
        }, ct);

    /// <summary>
    /// Creates the reversal of a posted journal, atomically: lock the original, refuse if a live
    /// reversal exists, insert the mirrored draft, submit it, commit. The original row is not written.
    /// </summary>
    public async Task<Journal> ReverseAsync(
        Guid ledgerId, Guid journalId, string? description, string actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockJournalAsync(ledgerId, journalId, ct);
        var original = await LoadAsync(ledgerId, journalId, ct);

        var existing = await db.Journals
            .Where(j => j.ReversesJournalId == journalId && j.Status != JournalStatus.Rejected)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            throw new LedgerDomainException(
                DomainErrorKind.Conflict, "JOURNAL_ALREADY_REVERSED", $"Journal {journalId} already has reversal {existing}.");
        }

        var accounts = await LockAndLoadAccountsAsync(original, ct);
        var now = time.GetUtcNow();
        var reversal = Journal.CreateReversal(original, accounts, description, actor, now);
        db.Journals.Add(reversal);
        await db.SaveChangesAsync(ct);

        reversal.Submit(actor, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return reversal;
    }

    internal async Task<Journal> InTransactionAsync(
        Guid ledgerId, Guid journalId, Func<Journal, Task> change, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockJournalAsync(ledgerId, journalId, ct);
        var journal = await LoadAsync(ledgerId, journalId, ct);

        await change(journal);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return journal;
    }

    private async Task<Journal> LoadAsync(Guid ledgerId, Guid journalId, CancellationToken ct) =>
        await db.Journals.Include(j => j.Entries)
            .SingleOrDefaultAsync(j => j.Id == journalId && j.LedgerId == ledgerId, ct)
        ?? throw NotFound.Journal(journalId);

    private async Task<Dictionary<Guid, Account>> LockAndLoadAccountsAsync(Journal journal, CancellationToken ct)
    {
        var accountIds = journal.Entries.Select(e => e.AccountId).Distinct().Order().ToArray();
        await db.LockAccountsForShareAsync(accountIds, ct);
        return await db.Accounts.Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
    }
}
