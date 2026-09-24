using System.Data;
using System.Diagnostics;
using LedgerCore.Ledger.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LedgerCore.Ledger.Api.Application;

/// <summary>Overall result of a reconciliation run. A run that could not complete is an error response, never a status.</summary>
internal enum ReconciliationStatus
{
    /// <summary>Every check passed on a complete scan.</summary>
    Healthy,

    /// <summary>At least one discrepancy was found. Nothing was changed.</summary>
    Discrepancy,
}

/// <summary>What kind of inconsistency a discrepancy is. Each one is impossible without bypassing the database guards.</summary>
internal enum DiscrepancyCategory
{
    /// <summary>A posted journal whose debits ≠ credits, or that lacks a debit or a credit.</summary>
    UnbalancedJournal,

    /// <summary>A currency whose posted debits ≠ posted credits.</summary>
    CurrencyImbalance,

    /// <summary>A posted reversal whose original is missing or not posted.</summary>
    ReversalOriginalNotPosted,

    /// <summary>A posted reversal that does not exactly mirror its original (a missing, extra or altered leg).</summary>
    ReversalMismatch,

    /// <summary>A posted entry on an account of another ledger or currency, or on no account.</summary>
    UnexpectedAccountMovement,

    /// <summary>A posted journal without an APPROVED policy decision recorded for it.</summary>
    MissingApprovalEvidence,

    /// <summary>A posted journal without its JournalPosted outbox event.</summary>
    MissingPostedEvent,

    /// <summary>A JournalPosted event whose policyDecisionId is not the journal's APPROVED decision.</summary>
    EventEvidenceMismatch,

    /// <summary>A JournalPosted event for a journal that is not POSTED.</summary>
    OrphanPostedEvent,

    /// <summary>A journal posted after idempotency claims became mandatory, but without one.</summary>
    MissingIdempotencyClaim,
}

/// <param name="Category">What is wrong.</param>
/// <param name="JournalId">The affected journal, when the finding is about one.</param>
/// <param name="AccountId">The affected account, when the finding is about one.</param>
/// <param name="Currency">The affected currency, for currency-level findings.</param>
internal sealed record Discrepancy(DiscrepancyCategory Category, Guid? JournalId, Guid? AccountId, string? Currency);

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

/// <param name="RunId">Identifies this run in logs and in the response.</param>
/// <param name="LedgerId">The reconciled ledger.</param>
/// <param name="GeneratedAt">When the snapshot was taken.</param>
/// <param name="Currencies">Posted totals per currency: the currencies examined.</param>
/// <param name="Discrepancies">Findings, at most <see cref="LedgerReconciliation.MaximumFindingsPerCategory"/> per category.</param>
/// <param name="DiscrepanciesTruncated">True when some category had more findings than are listed.</param>
/// <param name="LegacyPostingsWithoutClaim">
/// Journals posted before idempotency claims existed (Milestone 4). Expected history, not a discrepancy.
/// </param>
/// <param name="Accounts">Movement of every account in the ledger, including accounts with none.</param>
internal sealed record ReconciliationReport(
    Guid RunId,
    Guid LedgerId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<CurrencyTotals> Currencies,
    IReadOnlyList<Discrepancy> Discrepancies,
    bool DiscrepanciesTruncated,
    int LegacyPostingsWithoutClaim,
    IReadOnlyList<AccountMovement> Accounts)
{
    public ReconciliationStatus Status => Discrepancies.Count == 0 ? ReconciliationStatus.Healthy : ReconciliationStatus.Discrepancy;

    public bool Consistent => Status == ReconciliationStatus.Healthy;

    public IReadOnlyList<Guid> JournalsIn(DiscrepancyCategory category) =>
        [.. Discrepancies.Where(d => d.Category == category && d.JournalId is not null).Select(d => d.JournalId!.Value)];
}

/// <summary>
/// Ledger-internal reconciliation (Milestone 4, hardened in Milestone 5). It recomputes every check
/// from persisted POSTED journal entries with plain SQL, in one read-only REPEATABLE READ snapshot.
/// It never uses client totals, cached balances, policy-service data or outbox payloads as a source
/// of amounts; the outbox is only checked against the ledger.
/// <list type="bullet">
/// <item>Draft, pending, approved-but-unposted and rejected journals never contribute.</item>
/// <item>An original and its reversal both stay in the figures, and net to zero.</item>
/// <item>It only reports: posted history is never corrected.</item>
/// <item>A fixed number of queries, whatever the size of the ledger (no N+1).</item>
/// </list>
/// A failed query fails the run (an error response). A partial scan is never reported as HEALTHY.
/// </summary>
internal sealed partial class LedgerReconciliation(LedgerDbContext db, TimeProvider time, ILogger<LedgerReconciliation> logger)
{
    public const int MaximumFindingsPerCategory = 100;

    public async Task<ReconciliationReport> RunAsync(Guid ledgerId, CancellationToken ct)
    {
        var runId = Guid.CreateVersion7(time.GetUtcNow());
        var started = Stopwatch.GetTimestamp();
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

        // Every journal-level check in one statement: each branch yields (category, journal, account).
        // LIMIT per branch bounds the response; one extra row per branch reveals truncation.
        var limit = MaximumFindingsPerCategory + 1;
        var findings = await db.Database.SqlQuery<FindingRow>(
            $"""
            WITH posted AS (SELECT id, reverses_journal_id FROM journals WHERE ledger_id = {ledgerId} AND status = 'POSTED')
            (SELECT 'UNBALANCED_JOURNAL' AS category, p.id AS journal_id, NULL::uuid AS account_id
               FROM posted p LEFT JOIN journal_entries e ON e.journal_id = p.id
              GROUP BY p.id
             HAVING count(*) FILTER (WHERE e.direction = 'DEBIT') = 0
                 OR count(*) FILTER (WHERE e.direction = 'CREDIT') = 0
                 OR sum(e.amount) FILTER (WHERE e.direction = 'DEBIT') <> sum(e.amount) FILTER (WHERE e.direction = 'CREDIT')
              ORDER BY p.id LIMIT {limit})
            UNION ALL
            (SELECT 'REVERSAL_ORIGINAL_NOT_POSTED', r.id, NULL
               FROM posted r LEFT JOIN journals o ON o.id = r.reverses_journal_id
              WHERE r.reverses_journal_id IS NOT NULL AND o.status IS DISTINCT FROM 'POSTED'
              ORDER BY r.id LIMIT {limit})
            UNION ALL
            (SELECT 'REVERSAL_MISMATCH', r.id, NULL
               FROM posted r JOIN journals o ON o.id = r.reverses_journal_id AND o.status = 'POSTED'
              WHERE EXISTS (
                    (SELECT account_id, CASE direction WHEN 'DEBIT' THEN 'CREDIT' ELSE 'DEBIT' END, amount
                       FROM journal_entries WHERE journal_id = o.id
                     EXCEPT ALL
                     SELECT account_id, direction, amount FROM journal_entries WHERE journal_id = r.id)
                    UNION ALL
                    (SELECT account_id, direction, amount FROM journal_entries WHERE journal_id = r.id
                     EXCEPT ALL
                     SELECT account_id, CASE direction WHEN 'DEBIT' THEN 'CREDIT' ELSE 'DEBIT' END, amount
                       FROM journal_entries WHERE journal_id = o.id))
              ORDER BY r.id LIMIT {limit})
            UNION ALL
            (SELECT 'UNEXPECTED_ACCOUNT_MOVEMENT', e.journal_id, e.account_id
               FROM posted p JOIN journal_entries e ON e.journal_id = p.id
               LEFT JOIN accounts a ON a.id = e.account_id
              WHERE a.id IS NULL OR a.ledger_id <> e.ledger_id OR a.currency <> e.currency
              ORDER BY e.journal_id, e.account_id LIMIT {limit})
            UNION ALL
            (SELECT 'MISSING_APPROVAL_EVIDENCE', p.id, NULL
               FROM posted p
              WHERE NOT EXISTS (SELECT 1 FROM journal_policy_decisions d WHERE d.journal_id = p.id AND d.decision = 'APPROVED')
              ORDER BY p.id LIMIT {limit})
            UNION ALL
            (SELECT 'MISSING_POSTED_EVENT', p.id, NULL
               FROM posted p
              WHERE NOT EXISTS (SELECT 1 FROM outbox_events o WHERE o.aggregate_id = p.id AND o.event_type = 'JournalPosted')
              ORDER BY p.id LIMIT {limit})
            UNION ALL
            (SELECT 'EVENT_EVIDENCE_MISMATCH', p.id, NULL
               FROM posted p
               JOIN outbox_events o ON o.aggregate_id = p.id AND o.event_type = 'JournalPosted'
               LEFT JOIN journal_policy_decisions d ON d.journal_id = p.id AND d.decision = 'APPROVED'
              WHERE (o.payload ->> 'policyDecisionId') IS DISTINCT FROM d.decision_id::text
              ORDER BY p.id LIMIT {limit})
            UNION ALL
            (SELECT 'ORPHAN_POSTED_EVENT', j.id, NULL
               FROM outbox_events o JOIN journals j ON j.id = o.aggregate_id
              WHERE o.event_type = 'JournalPosted' AND j.ledger_id = {ledgerId} AND j.status <> 'POSTED'
              ORDER BY j.id LIMIT {limit})
            UNION ALL
            -- Posted since claims became mandatory (the audit row carries a key), yet no claim.
            (SELECT 'MISSING_IDEMPOTENCY_CLAIM', p.id, NULL
               FROM posted p JOIN journal_status_transitions t ON t.journal_id = p.id AND t.to_status = 'POSTED'
              WHERE t.idempotency_key IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM command_idempotency c WHERE c.operation = 'POST_JOURNAL' AND c.target_journal_id = p.id)
              ORDER BY p.id LIMIT {limit})
            """).ToListAsync(ct);

        // Posted before Milestone 4: no claim, and an audit row from before the key column existed.
        var legacy = await db.Database.SqlQuery<int>(
            $"""
            SELECT count(*)::int AS "Value"
              FROM journals j JOIN journal_status_transitions t ON t.journal_id = j.id AND t.to_status = 'POSTED'
             WHERE j.ledger_id = {ledgerId} AND j.status = 'POSTED' AND t.idempotency_key IS NULL
               AND NOT EXISTS (SELECT 1 FROM command_idempotency c WHERE c.operation = 'POST_JOURNAL' AND c.target_journal_id = j.id)
            """).SingleAsync(ct);

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

        var totals = currencies.Select(c => new CurrencyTotals(c.Currency, c.PostedJournals, c.Debits, c.Credits)).ToList();
        var truncated = findings.GroupBy(f => f.Category).Any(g => g.Count() > MaximumFindingsPerCategory);
        var discrepancies = findings
            .GroupBy(f => f.Category)
            .SelectMany(g => g.Take(MaximumFindingsPerCategory))
            .Select(f => new Discrepancy(EnumText.Parse<DiscrepancyCategory>(f.Category), f.JournalId, f.AccountId, null))
            .Concat(totals.Where(c => !c.Balanced).Select(c => new Discrepancy(DiscrepancyCategory.CurrencyImbalance, null, null, c.Currency)))
            .ToList();

        var report = new ReconciliationReport(
            runId,
            ledgerId,
            time.GetUtcNow(),
            totals,
            discrepancies,
            truncated,
            legacy,
            [.. accounts.Select(a => new AccountMovement(a.AccountId, a.Code, a.Currency, a.Debits, a.Credits))]);
        Log(report, Stopwatch.GetElapsedTime(started));
        return report;
    }

    /// <summary>Identifiers only: never amounts, payloads, keys or credentials.</summary>
    private void Log(ReconciliationReport report, TimeSpan duration)
    {
        var durationMs = (long)duration.TotalMilliseconds;
        if (report.Status == ReconciliationStatus.Healthy)
        {
            LogHealthy(report.RunId, report.LedgerId, report.Currencies.Count, report.LegacyPostingsWithoutClaim, durationMs);
            return;
        }

        foreach (var group in report.Discrepancies.GroupBy(d => d.Category))
        {
            var sample = string.Join(",", group.Take(10).Select(d => d.JournalId?.ToString() ?? d.Currency));
            LogDiscrepancy(report.RunId, report.LedgerId, EnumText.ToText(group.Key), group.Count(), sample);
        }

        LogDiscrepancies(report.RunId, report.LedgerId, report.Discrepancies.Count, report.DiscrepanciesTruncated, durationMs);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation {ReconciliationRunId} of ledger {LedgerId}: HEALTHY ({CurrencyCount} currencies, {LegacyPostings} legacy postings) in {DurationMs} ms")]
    private partial void LogHealthy(Guid reconciliationRunId, Guid ledgerId, int currencyCount, int legacyPostings, long durationMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciliation {ReconciliationRunId} of ledger {LedgerId}: {DiscrepancyCategory} x{DiscrepancyCount} (first: {AffectedIds})")]
    private partial void LogDiscrepancy(Guid reconciliationRunId, Guid ledgerId, string discrepancyCategory, int discrepancyCount, string affectedIds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciliation {ReconciliationRunId} of ledger {LedgerId}: DISCREPANCY ({DiscrepancyCount} findings, truncated {Truncated}) in {DurationMs} ms")]
    private partial void LogDiscrepancies(Guid reconciliationRunId, Guid ledgerId, int discrepancyCount, bool truncated, long durationMs);

    private sealed class CurrencyRow
    {
        public string Currency { get; init; } = null!;

        public int PostedJournals { get; init; }

        public decimal Debits { get; init; }

        public decimal Credits { get; init; }
    }

    private sealed class FindingRow
    {
        public string Category { get; init; } = null!;

        public Guid? JournalId { get; init; }

        public Guid? AccountId { get; init; }
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
