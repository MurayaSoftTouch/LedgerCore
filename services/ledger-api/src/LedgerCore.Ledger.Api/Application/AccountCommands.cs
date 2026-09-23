using LedgerCore.Ledger.Api.Persistence;
using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Monetary;
using Microsoft.EntityFrameworkCore;
using DomainLedger = LedgerCore.Ledger.Domain.Ledgers.Ledger;

namespace LedgerCore.Ledger.Api.Application;

internal sealed class AccountCommands(LedgerDbContext db, TimeProvider time)
{
    public async Task<DomainLedger> CreateLedgerAsync(string code, string name, CancellationToken ct)
    {
        var ledger = DomainLedger.Create(code, name, time.GetUtcNow());
        db.Ledgers.Add(ledger);
        await db.SaveChangesAsync(ct);
        return ledger;
    }

    public async Task<Account> OpenAccountAsync(
        Guid ledgerId, string code, string name, AccountType type, string currency, CancellationToken ct)
    {
        if (!await db.Ledgers.AnyAsync(l => l.Id == ledgerId, ct))
        {
            throw NotFound.Ledger(ledgerId);
        }

        var account = Account.Open(ledgerId, code, name, type, Currency.FromCode(currency), time.GetUtcNow());
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<Account> DeactivateAccountAsync(Guid ledgerId, Guid accountId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM accounts WHERE id = {accountId} AND ledger_id = {ledgerId} FOR UPDATE", ct);
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId && a.LedgerId == ledgerId, ct)
            ?? throw NotFound.Account(accountId);

        account.Deactivate(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return account;
    }
}
