using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using LedgerCore.Ledger.Api.Application;
using LedgerCore.Ledger.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>
/// Reconciliation findings (Milestone 5). Every discrepancy here is impossible through the product:
/// each test corrupts a disposable test database as the superuser with triggers disabled
/// (<c>session_replication_role = replica</c>), then shows the report detects it, logs it with
/// identifiers only, and changes nothing. Production guards are not weakened for this.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ReconciliationDiscrepancyTests(PostgresFixture db) : IDisposable
{
    private readonly CollectingLoggerProvider _logs = new();

    public void Dispose() => _logs.Dispose();

    private async Task<ReconciliationReport> ReconcileAsync(Guid ledgerId, params IInterceptor[] interceptors)
    {
        await using var ctx = db.CreateRuntimeContext(interceptors);
        return await new LedgerReconciliation(ctx, TimeProvider.System, _logs.CreateLogger<LedgerReconciliation>()).RunAsync(ledgerId, default);
    }

    /// <summary>Runs SQL as the superuser with every trigger and foreign-key check disabled.</summary>
    private async Task CorruptAsync(string sql, params object[] args)
    {
        await using var superuser = db.CreateSuperuserConnection();
        await superuser.OpenAsync();
        await using var tx = await superuser.BeginTransactionAsync();
        await Sql.ExecuteAsync(superuser, "SET LOCAL session_replication_role = replica");
        await Sql.ExecuteAsync(superuser, sql, args);
        await tx.CommitAsync();
    }

    private static IReadOnlyList<DiscrepancyCategory> Categories(ReconciliationReport report) =>
        [.. report.Discrepancies.Select(d => d.Category).Distinct().Order()];

    [Fact]
    public async Task ACleanLedgerIsHealthy()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var reversal = await s.ReverseAsync(original);
        await s.ApproveAsync(reversal.Id);
        await s.PostAsync(reversal.Id);

        var report = await ReconcileAsync(s.LedgerId);

        Assert.Equal(ReconciliationStatus.Healthy, report.Status);
        Assert.Empty(report.Discrepancies);
        Assert.Equal(0, report.LegacyPostingsWithoutClaim);
        Assert.NotEqual(Guid.Empty, report.RunId);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains($"Reconciliation {report.RunId}", StringComparison.Ordinal) && e.Message.Contains("HEALTHY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnbalancedJournalAndItsCurrencyImbalanceAreReported()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await CorruptAsync(
            "INSERT INTO journal_entries (id, journal_id, ledger_id, currency, line_number, account_id, direction, amount) VALUES (gen_random_uuid(), $1, $2, 'KES', 99, $3, 'DEBIT', 5)",
            id,
            s.LedgerId,
            s.Rent);

        var report = await ReconcileAsync(s.LedgerId);

        Assert.Equal(ReconciliationStatus.Discrepancy, report.Status);
        Assert.Equal([DiscrepancyCategory.UnbalancedJournal, DiscrepancyCategory.CurrencyImbalance], Categories(report));
        Assert.Equal([id], report.JournalsIn(DiscrepancyCategory.UnbalancedJournal));
        Assert.Equal("KES", report.Discrepancies.Single(d => d.Category == DiscrepancyCategory.CurrencyImbalance).Currency);
        var warning = Assert.Single(_logs.Entries, e => e.Message.Contains("UNBALANCED_JOURNAL", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(report.RunId.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.Contains(id.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("10.00", string.Join('\n', _logs.Entries.Select(e => e.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReversalMissingALegIsAMismatch()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 4m), (s.Bank, Credit, 6m));
        var reversal = await s.ReverseAsync(original);
        await s.ApproveAsync(reversal.Id);
        await s.PostAsync(reversal.Id);
        await CorruptAsync("DELETE FROM journal_entries WHERE journal_id = $1 AND account_id = $2", reversal.Id, s.Bank);

        var report = await ReconcileAsync(s.LedgerId);

        Assert.Equal([reversal.Id], report.JournalsIn(DiscrepancyCategory.ReversalMismatch));
        Assert.Equal([reversal.Id], report.JournalsIn(DiscrepancyCategory.UnbalancedJournal));
    }

    [Fact]
    public async Task APostedReversalOfAnUnpostedOriginalIsReported()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var reversal = await s.ReverseAsync(original);
        await s.ApproveAsync(reversal.Id);
        await s.PostAsync(reversal.Id);
        await CorruptAsync("UPDATE journals SET status = 'APPROVED', posted_at = NULL, posted_by = NULL WHERE id = $1", original);

        var report = await ReconcileAsync(s.LedgerId);

        Assert.Equal([reversal.Id], report.JournalsIn(DiscrepancyCategory.ReversalOriginalNotPosted));
        Assert.Equal([original], report.JournalsIn(DiscrepancyCategory.OrphanPostedEvent));
    }

    [Fact]
    public async Task MovementOnAnAccountOfAnotherLedgerIsReported()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var other = await LedgerScenario.CreateAsync(db);
        var id = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await CorruptAsync("UPDATE journal_entries SET account_id = $2 WHERE journal_id = $1 AND direction = 'DEBIT'", id, other.Rent);

        var report = await ReconcileAsync(s.LedgerId);

        var finding = Assert.Single(report.Discrepancies, d => d.Category == DiscrepancyCategory.UnexpectedAccountMovement);
        Assert.Equal((id, other.Rent), (finding.JournalId, finding.AccountId));
    }

    [Fact]
    public async Task MissingEvidenceAndAnEventThatNoLongerMatchesAreReported()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await CorruptAsync("DELETE FROM journal_policy_decisions WHERE journal_id = $1", id);

        var report = await ReconcileAsync(s.LedgerId);

        Assert.Equal([DiscrepancyCategory.MissingApprovalEvidence, DiscrepancyCategory.EventEvidenceMismatch], Categories(report));
        Assert.Equal([id], report.JournalsIn(DiscrepancyCategory.MissingApprovalEvidence));
    }

    [Fact]
    public async Task AMissingOrAlteredPostedEventIsReported()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var missing = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var altered = await s.PostedAsync((s.Rent, Debit, 20m), (s.Cash, Credit, 20m));
        await CorruptAsync("DELETE FROM outbox_events WHERE aggregate_id = $1", missing);
        await CorruptAsync("UPDATE outbox_events SET payload = jsonb_set(payload, '{policyDecisionId}', to_jsonb(gen_random_uuid()::text)) WHERE aggregate_id = $1", altered);

        var report = await ReconcileAsync(s.LedgerId);

        Assert.Equal([missing], report.JournalsIn(DiscrepancyCategory.MissingPostedEvent));
        Assert.Equal([altered], report.JournalsIn(DiscrepancyCategory.EventEvidenceMismatch));
    }

    [Fact]
    public async Task AnEventForAnUnpostedJournalIsAnOrphan()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var approved = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await CorruptAsync(
            "INSERT INTO outbox_events (id, aggregate_type, aggregate_id, event_type, payload, created_at) VALUES (gen_random_uuid(), 'Journal', $1, 'JournalPosted', '{}', now())",
            approved);

        var report = await ReconcileAsync(s.LedgerId);

        Assert.Equal([approved], report.JournalsIn(DiscrepancyCategory.OrphanPostedEvent));
    }

    [Fact]
    public async Task LegacyPostingsAreHistoryButAMissingModernClaimIsADiscrepancy()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var legacy = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var modern = await s.PostedAsync((s.Rent, Debit, 20m), (s.Cash, Credit, 20m));

        // Legacy: as if posted before Milestone 4 (no claim, and an audit row without a key).
        await CorruptAsync("DELETE FROM command_idempotency WHERE target_journal_id = $1", legacy);
        await CorruptAsync("UPDATE journal_status_transitions SET idempotency_key = NULL WHERE journal_id = $1 AND to_status = 'POSTED'", legacy);
        var onlyLegacy = await ReconcileAsync(s.LedgerId);

        Assert.Equal(ReconciliationStatus.Healthy, onlyLegacy.Status);
        Assert.Equal(1, onlyLegacy.LegacyPostingsWithoutClaim);
        Assert.Equal(legacy, (await s.GetAsync(legacy)).Journal.Id); // legacy rows stay readable

        // Corruption: posted with a key (the audit says so), but the claim is gone.
        await CorruptAsync("DELETE FROM command_idempotency WHERE target_journal_id = $1", modern);
        var report = await ReconcileAsync(s.LedgerId);

        Assert.Equal([modern], report.JournalsIn(DiscrepancyCategory.MissingIdempotencyClaim));
        Assert.Equal(1, report.LegacyPostingsWithoutClaim);
    }

    [Fact]
    public async Task FindingsAreBoundedAndTruncationIsFlagged()
    {
        var s = await LedgerScenario.CreateAsync(db);
        await BulkPostedAsync(s, LedgerReconciliation.MaximumFindingsPerCategory + 20, balanced: false);

        var report = await ReconcileAsync(s.LedgerId);

        Assert.True(report.DiscrepanciesTruncated);
        Assert.Equal(LedgerReconciliation.MaximumFindingsPerCategory, report.JournalsIn(DiscrepancyCategory.UnbalancedJournal).Count);
    }

    [Fact]
    public async Task ReconciliationNeverModifiesData()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await CorruptAsync("DELETE FROM outbox_events WHERE aggregate_id = $1", id);
        var before = await FingerprintAsync(s.LedgerId);

        await ReconcileAsync(s.LedgerId);
        await ReconcileAsync(s.LedgerId);

        Assert.Equal(before, await FingerprintAsync(s.LedgerId));
    }

    [Fact]
    public async Task QueryCountIsConstantAndALargeLedgerReconcilesQuickly()
    {
        var small = await LedgerScenario.CreateAsync(db);
        await small.PostedAsync((small.Rent, Debit, 10m), (small.Cash, Credit, 10m));
        var large = await LedgerScenario.CreateAsync(db);
        await BulkPostedAsync(large, 5_000, balanced: true);
        var smallCount = new CommandCounter();
        var largeCount = new CommandCounter();

        await ReconcileAsync(small.LedgerId, smallCount);
        var stopwatch = Stopwatch.StartNew();
        var report = await ReconcileAsync(large.LedgerId, largeCount);
        stopwatch.Stop();

        Assert.Equal(ReconciliationStatus.Healthy, report.Status);
        Assert.Equal(5_000, report.Currencies.Single().PostedJournals);
        Assert.Equal(smallCount.Count, largeCount.Count);
        Assert.True(largeCount.Count <= 8, $"{largeCount.Count} commands");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"took {stopwatch.Elapsed}");
    }

    /// <summary>
    /// Inserts posted journals directly (guards off): two entries each (balanced) or one debit only,
    /// with APPROVED evidence and a matching JournalPosted event, so balanced ones are consistent.
    /// </summary>
    private Task BulkPostedAsync(LedgerScenario s, int count, bool balanced) =>
        CorruptAsync(
            $$"""
            WITH j AS (
                INSERT INTO journals (id, ledger_id, currency, transaction_type, description, status, created_at, created_by,
                                      submitted_at, submitted_by, approved_at, approved_by, posted_at, posted_by)
                SELECT gen_random_uuid(), $1, 'KES', 'PAYMENT', 'bulk ' || n, 'POSTED', now(), 'bulk', now(), 'bulk', now(), 'bulk', now(), 'bulk'
                  FROM generate_series(1, {{count}}) n
                RETURNING id),
            d AS (
                INSERT INTO journal_policy_decisions (journal_id, decision_id, policy_version, decision, reason_codes, evaluated_at, contract_version, received_at)
                SELECT id, gen_random_uuid(), 'bulk@1', 'APPROVED', '{}', now(), '1.1.0', now() FROM j
                RETURNING journal_id, decision_id),
            o AS (
                INSERT INTO outbox_events (id, aggregate_type, aggregate_id, event_type, payload, created_at)
                SELECT gen_random_uuid(), 'Journal', journal_id, 'JournalPosted', jsonb_build_object('policyDecisionId', decision_id::text), now() FROM d),
            debit AS (
                INSERT INTO journal_entries (id, journal_id, ledger_id, currency, line_number, account_id, direction, amount)
                SELECT gen_random_uuid(), id, $1, 'KES', 1, $2, 'DEBIT', 12.34 FROM j)
            INSERT INTO journal_entries (id, journal_id, ledger_id, currency, line_number, account_id, direction, amount)
            SELECT gen_random_uuid(), id, $1, 'KES', 2, $3, 'CREDIT', 12.34 FROM j WHERE {{(balanced ? "true" : "false")}}
            """,
            s.LedgerId,
            s.Rent,
            s.Cash);

    private async Task<string?> FingerprintAsync(Guid ledgerId)
    {
        await using var connection = db.CreateSuperuserConnection();
        return await Sql.ScalarAsync<string>(
            connection,
            """
            SELECT md5(concat_ws('|',
                (SELECT string_agg(j::text, ',' ORDER BY j.id) FROM journals j WHERE ledger_id = $1),
                (SELECT string_agg(e::text, ',' ORDER BY e.id) FROM journal_entries e WHERE ledger_id = $1),
                (SELECT string_agg(d::text, ',' ORDER BY d.journal_id) FROM journal_policy_decisions d JOIN journals j ON j.id = d.journal_id WHERE j.ledger_id = $1),
                (SELECT string_agg(o::text, ',' ORDER BY o.id) FROM outbox_events o JOIN journals j ON j.id = o.aggregate_id WHERE j.ledger_id = $1),
                (SELECT string_agg(c::text, ',' ORDER BY c.idempotency_key) FROM command_idempotency c WHERE ledger_id = $1),
                (SELECT string_agg(t::text, ',' ORDER BY t.id) FROM journal_status_transitions t JOIN journals j ON j.id = t.journal_id WHERE j.ledger_id = $1)))
            """,
            ledgerId);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => _count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }
}

/// <summary>Collects formatted log messages in memory for assertions.</summary>
internal sealed class CollectingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public ILogger<T> CreateLogger<T>() => new Logger<T>(new Factory(this));

    public void Dispose()
    {
    }

    private sealed class Factory(CollectingLoggerProvider provider) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void AddProvider(ILoggerProvider loggerProvider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class Logger(CollectingLoggerProvider provider, string category) : ILogger
    {
        /// <summary>Scope values are recorded too, so leak checks cover them.</summary>
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            var text = state is IEnumerable<KeyValuePair<string, object>> pairs
                ? string.Join(", ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : state.ToString();
            provider.Entries.Enqueue((LogLevel.Trace, category, "SCOPE " + text));
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider.Entries.Enqueue((logLevel, category, formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception)));
    }
}
