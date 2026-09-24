using System.Data;
using LedgerCore.Ledger.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LedgerCore.Ledger.Api.Application;

/// <param name="Currency">ISO 4217 code. Totals never mix currencies.</param>
/// <param name="PostedJournals">Posted journals in this currency.</param>
/// <param name="Debits">Sum of posted debit amounts.</param>
/// <param name="Credits">Sum of posted credit amounts.</param>
internal sealed record CurrencyTotals(string Currency, int PostedJournals, decimal Debits, decimal Credits)
{
    public bool Balanced => Debits == Credits;
}

/// <summary>Movement of one account, from POSTED entries only. Net is debits − credits.</summary>
internal sealed record AccountMovement(Guid AccountId, string Code, string Currency, decimal Debits, decimal Credits)
{
    public decimal Net => Debits - Credits;
}

/// <param name="LedgerId">The reconciled ledger.</param>
/// <param name="GeneratedAt">When the snapshot was taken.</param>
/// <param name="Currencies">Posted totals per currency.</param>
/// <param name="UnbalancedJournals">Posted journals whose debits ≠ credits, or lacking a debit or a credit. Must be empty.</param>
/// <param name="MismatchedReversals">Posted reversals that don't exactly mirror a posted original. Must be empty.</param>
/// <param name="Accounts">Movement of every account in the ledger, including accounts with none.</param>
internal sealed record ReconciliationReport(
    Guid LedgerId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<CurrencyTotals> Currencies,
    IReadOnlyList<Guid> UnbalancedJournals,
    IReadOnlyList<Guid> MismatchedReversals,
    IReadOnlyList<AccountMovement> Accounts)
{
    public bool Consistent =>
        UnbalancedJournals.Count == 0 && MismatchedReversals.Count == 0 && Currencies.All(c => c.Balanced)
        && Accounts.GroupBy(a => a.Currency).All(g => g.Sum(a => a.Net) == 0m);
}

/// <summary>
/// Ledger-internal reconciliation (Milestone 4): recomputes every check from POSTED journal entries
/// with plain SQL, independently of the posting code, in one read-only REPEATABLE READ snapshot.
/// Draft, pending, approved-but-unposted and rejected journals never contribute. A reversal and its
/// original both stay in the figures; together they net to zero. The database guards make an
/// inconsistent report impossible without bypassing them, so a non-empty finding means exactly that.
/// Scheduled and cross-system reconciliation are Milestone 5.
/// </summary>
internal sealed class LedgerReconciliation(LedgerDbContext db, TimeProvider time)
{
    public async Task<ReconciliationReport> RunAsync(Guid ledgerId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        if (!await db.Ledgers.AnyAsync(l => l.Id == ledgerId, ct))
        {
            throw NotFound.Ledger(ledgerId);
        }

        // Column aliases follow the snake_case naming convention EF applies to result types too.
        var currencies = await db.Database.SqlQuery<CurrencyRow>(
            $"""
            SELECT j.currency::text AS currency,
                   count(DISTINCT j.id)::int AS posted_journals,
                   coalesce(sum(e.amount) FILTER (WHERE e.direction = 'DEBIT'), 0) AS debits,
                   coalesce(sum(e.amount) FILTER (WHERE e.direction = 'CREDIT'), 0) AS credits
              FROM journals j JOIN journal_entries e ON e.journal_id = j.id
             WHERE j.ledger_id = {ledgerId} AND j.status = 'POSTED'
             GROUP BY j.currency
             ORDER BY j.currency
            """).ToListAsync(ct);

        var unbalanced = await db.Database.SqlQuery<Guid>(
            $"""
            SELECT j.id AS "Value"
              FROM journals j LEFT JOIN journal_entries e ON e.journal_id = j.id
             WHERE j.ledger_id = {ledgerId} AND j.status = 'POSTED'
             GROUP BY j.id
            HAVING count(*) FILTER (WHERE e.direction = 'DEBIT') = 0
                OR count(*) FILTER (WHERE e.direction = 'CREDIT') = 0
                OR sum(e.amount) FILTER (WHERE e.direction = 'DEBIT') <> sum(e.amount) FILTER (WHERE e.direction = 'CREDIT')
             ORDER BY j.id
            """).ToListAsync(ct);

        // A posted reversal must reverse a posted original, with the same accounts and amounts and
        // every direction swapped (multiset equality in both directions).
        var mismatched = await db.Database.SqlQuery<Guid>(
            $"""
            SELECT r.id AS "Value"
              FROM journals r JOIN journals o ON o.id = r.reverses_journal_id
             WHERE r.ledger_id = {ledgerId} AND r.status = 'POSTED'
               AND (o.status <> 'POSTED' OR EXISTS (
                    (SELECT account_id, CASE direction WHEN 'DEBIT' THEN 'CREDIT' ELSE 'DEBIT' END, amount
                       FROM journal_entries WHERE journal_id = o.id
                     EXCEPT ALL
                     SELECT account_id, direction, amount FROM journal_entries WHERE journal_id = r.id)
                    UNION ALL
                    (SELECT account_id, direction, amount FROM journal_entries WHERE journal_id = r.id
                     EXCEPT ALL
                     SELECT account_id, CASE direction WHEN 'DEBIT' THEN 'CREDIT' ELSE 'DEBIT' END, amount
                       FROM journal_entries WHERE journal_id = o.id)))
             ORDER BY r.id
            """).ToListAsync(ct);

        var accounts = await db.Database.SqlQuery<AccountRow>(
            $"""
            SELECT a.id AS account_id, a.code AS code, a.currency::text AS currency,
                   coalesce(sum(e.amount) FILTER (WHERE e.direction = 'DEBIT' AND j.status = 'POSTED'), 0) AS debits,
                   coalesce(sum(e.amount) FILTER (WHERE e.direction = 'CREDIT' AND j.status = 'POSTED'), 0) AS credits
              FROM accounts a
              LEFT JOIN journal_entries e ON e.account_id = a.id
              LEFT JOIN journals j ON j.id = e.journal_id
             WHERE a.ledger_id = {ledgerId}
             GROUP BY a.id, a.code, a.currency
             ORDER BY a.code
            """).ToListAsync(ct);

        return new ReconciliationReport(
            ledgerId,
            time.GetUtcNow(),
            [.. currencies.Select(c => new CurrencyTotals(c.Currency, c.PostedJournals, c.Debits, c.Credits))],
            unbalanced,
            mismatched,
            [.. accounts.Select(a => new AccountMovement(a.AccountId, a.Code, a.Currency, a.Debits, a.Credits))]);
    }

    private sealed class CurrencyRow
    {
        public string Currency { get; init; } = null!;

        public int PostedJournals { get; init; }

        public decimal Debits { get; init; }

        public decimal Credits { get; init; }
    }

    private sealed class AccountRow
    {
        public Guid AccountId { get; init; }

        public string Code { get; init; } = null!;

        public string Currency { get; init; } = null!;

        public decimal Debits { get; init; }

        public decimal Credits { get; init; }
    }
}
