using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Tests.Infrastructure;
using LedgerCore.Ledger.Domain;
using LedgerCore.Ledger.Domain.Journals;
using Npgsql;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>Idempotent reversal (ADR-015) against real PostgreSQL. Reversals still go through policy (ADR-006).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ReversalIdempotencyTests(PostgresFixture db)
{
    private async Task<long> CountAsync(string sql, Guid journal)
    {
        await using var connection = db.CreateRuntimeConnection();
        return await Sql.ScalarAsync<long>(connection, sql, journal);
    }

    private Task<long> ReversalsOfAsync(Guid original) =>
        CountAsync("SELECT count(*) FROM journals WHERE reverses_journal_id = $1", original);

    private Task<long> ReversalClaimsAsync(Guid original) =>
        CountAsync("SELECT count(*) FROM command_idempotency WHERE target_journal_id = $1 AND operation = 'REVERSE_JOURNAL'", original);

    private async Task<string?> SnapshotAsync(Guid journalId)
    {
        await using var connection = db.CreateRuntimeConnection();
        return await Sql.ScalarAsync<string>(
            connection,
            "SELECT (SELECT row_to_json(j)::text FROM journals j WHERE j.id = $1) || " +
            "(SELECT json_agg(e ORDER BY e.line_number)::text FROM journal_entries e WHERE e.journal_id = $1)",
            journalId);
    }

    [Fact]
    public async Task IdenticalRetryReturnsTheSameReversal()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var key = LedgerScenario.NewKey();

        var first = await s.ReverseWithKeyAsync(original, key, "wrong period");
        var retry = await s.ReverseWithKeyAsync(original, key, "wrong period");

        Assert.False(first.Replayed);
        Assert.True(retry.Replayed);
        Assert.Equal(first.Journal.Id, retry.Journal.Id);
        Assert.Equal(JournalStatus.PendingApproval, retry.Journal.Status);
        Assert.Equal(1, await ReversalsOfAsync(original));
        Assert.Equal(1, await ReversalClaimsAsync(original));

        // The replay follows the reversal's lifecycle: after approval and posting it is the same journal.
        await s.ApproveAsync(first.Journal.Id);
        await s.PostAsync(first.Journal.Id);
        var later = await s.ReverseWithKeyAsync(original, key, "wrong period");
        Assert.Equal(first.Journal.Id, later.Journal.Id);
        Assert.Equal(JournalStatus.Posted, later.Journal.Status);
    }

    [Fact]
    public async Task SameKeyWithAnotherDescriptionIsAConflict()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var key = LedgerScenario.NewKey();
        await s.ReverseWithKeyAsync(original, key, "wrong period");

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.ReverseWithKeyAsync(original, key, "duplicate invoice"));

        Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
        Assert.Equal(1, await ReversalsOfAsync(original));
    }

    [Fact]
    public async Task SameKeyForAnotherOriginalIsAConflict()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var first = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var second = await s.PostedAsync((s.Rent, Debit, 20m), (s.Cash, Credit, 20m));
        var key = LedgerScenario.NewKey();
        await s.ReverseWithKeyAsync(first, key);

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.ReverseWithKeyAsync(second, key));

        Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
        Assert.Equal(0, await ReversalsOfAsync(second));
    }

    [Fact]
    public async Task APostingKeyAndAReversalKeyAreIndependent()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var key = LedgerScenario.NewKey();
        await s.PostWithKeyAsync(id, key);

        // The key is scoped by operation: reusing it for the reversal is not a replay of the posting.
        var reversal = await s.ReverseWithKeyAsync(id, key);

        Assert.False(reversal.Replayed);
        Assert.Equal(id, reversal.Journal.ReversesJournalId);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsCreateOneReversal()
    {
        const int callers = 8;
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var key = LedgerScenario.NewKey();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, callers).Select(async _ =>
        {
            await start.Task;
            return await s.ReverseWithKeyAsync(original, key);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, r => !r.Replayed);
        Assert.Single(results.Select(r => r.Journal.Id).Distinct());
        Assert.Equal(1, await ReversalsOfAsync(original));
        Assert.Equal(1, await ReversalClaimsAsync(original));
    }

    [Fact]
    public async Task ConcurrentRequestsWithDifferentKeysCreateOneLiveReversal()
    {
        const int callers = 8;
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, callers).Select(async _ =>
        {
            await start.Task;
            try
            {
                await s.ReverseWithKeyAsync(original, LedgerScenario.NewKey());
                return "reversed";
            }
            catch (LedgerDomainException e)
            {
                return e.Code;
            }
        }).ToArray();
        start.SetResult();
        var outcomes = await Task.WhenAll(tasks);

        Assert.Single(outcomes, o => o == "reversed");
        Assert.All(outcomes.Where(o => o != "reversed"), o => Assert.Equal("JOURNAL_ALREADY_REVERSED", o));
        Assert.Equal(1, await ReversalsOfAsync(original));
        Assert.Equal(1, await ReversalClaimsAsync(original));
    }

    [Fact]
    public async Task TheOriginalIsNeverWritten()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var before = await SnapshotAsync(original);
        var key = LedgerScenario.NewKey();

        var reversal = await s.ReverseWithKeyAsync(original, key);
        await s.ReverseWithKeyAsync(original, key);
        await s.ApproveAsync(reversal.Journal.Id);
        await s.PostAsync(reversal.Journal.Id);

        Assert.Equal(before, await SnapshotAsync(original));
    }

    [Fact]
    public async Task ARejectedReversalIsReplayedForItsKeyAndANewKeyMayReverseAgain()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var key = LedgerScenario.NewKey();
        var rejected = await s.ReverseWithKeyAsync(original, key);
        await s.RejectAsync(rejected.Journal.Id);

        var replay = await s.ReverseWithKeyAsync(original, key);
        Assert.Equal(rejected.Journal.Id, replay.Journal.Id);
        Assert.Equal(JournalStatus.Rejected, replay.Journal.Status);

        var second = await s.ReverseWithKeyAsync(original, LedgerScenario.NewKey());
        Assert.NotEqual(rejected.Journal.Id, second.Journal.Id);
        Assert.Equal(2, await ReversalsOfAsync(original));
    }

    [Fact]
    public async Task ReversalsNeedApprovalAndCannotPostWhenRejectedOrUnderReview()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var first = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var second = await s.PostedAsync((s.Rent, Debit, 20m), (s.Cash, Credit, 20m));
        var underReview = (await s.ReverseWithKeyAsync(first, LedgerScenario.NewKey())).Journal.Id;
        var rejected = (await s.ReverseWithKeyAsync(second, LedgerScenario.NewKey())).Journal.Id;
        s.Policy.Decide(underReview, PolicyDecisionValue.ReviewRequired, "MANUAL");
        await s.RequestApprovalAsync(underReview);
        await s.RejectAsync(rejected);

        Assert.Equal("REVERSAL", s.Policy.Requests.First(r => r.TransactionId == underReview).TransactionType);
        foreach (var id in new[] { underReview, rejected })
        {
            var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostAsync(id));
            Assert.Equal("JOURNAL_INVALID_STATE", error.Code);
        }
    }

    [Fact]
    public async Task TheReversalsAuditCarriesItsKey()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var key = LedgerScenario.NewKey();

        var reversal = await s.ReverseWithKeyAsync(original, key);

        await using var connection = db.CreateRuntimeConnection();
        Assert.Equal(
            key,
            await Sql.ScalarAsync<string>(
                connection,
                "SELECT idempotency_key FROM journal_status_transitions WHERE journal_id = $1 AND to_status = 'PENDING_APPROVAL'",
                reversal.Journal.Id));
    }

    [Theory]
    [InlineData("INSERT INTO command_idempotency")]
    [InlineData("UPDATE journals SET")]
    public async Task InjectedFailureLeavesNoReversalAndTheKeyStillWorks(string failingStatement)
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var key = LedgerScenario.NewKey();

        await Injected.FailureAsync(() => s.ReverseWithKeyAsync(original, key, null, new FailingCommandInterceptor(failingStatement)));
        Assert.Equal(0, await ReversalsOfAsync(original));
        Assert.Equal(0, await ReversalClaimsAsync(original));

        await Injected.FailureAsync(() => s.ReverseWithKeyAsync(original, key, null, new FailingCommitInterceptor()));
        Assert.Equal(0, await ReversalsOfAsync(original));
        Assert.Equal(0, await ReversalClaimsAsync(original));

        Assert.False((await s.ReverseWithKeyAsync(original, key)).Replayed);
        Assert.Equal(1, await ReversalsOfAsync(original));
    }

    [Fact]
    public async Task DatabaseRefusesAReversalWithoutAClaim()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await using var owner = db.CreateOwnerConnection();
        await owner.OpenAsync();
        await using var tx = await owner.BeginTransactionAsync();

        // A correctly mirrored reversal, inserted and submitted by SQL, but with no reversal claim.
        var reversal = Guid.NewGuid();
        await Sql.ExecuteAsync(
            owner,
            "INSERT INTO journals (id, ledger_id, currency, transaction_type, description, status, reverses_journal_id, created_at, created_by) " +
            "SELECT $1, ledger_id, currency, 'REVERSAL', 'sql reversal', 'DRAFT', id, now(), 'sql' FROM journals WHERE id = $2",
            reversal,
            original);
        await Sql.ExecuteAsync(
            owner,
            "INSERT INTO journal_entries (id, journal_id, ledger_id, currency, line_number, account_id, direction, amount) " +
            "SELECT gen_random_uuid(), $1, ledger_id, currency, line_number, account_id, CASE direction WHEN 'DEBIT' THEN 'CREDIT' ELSE 'DEBIT' END, amount " +
            "FROM journal_entries WHERE journal_id = $2",
            reversal,
            original);
        await Sql.ExecuteAsync(owner, "UPDATE journals SET status = 'PENDING_APPROVAL', submitted_at = now(), submitted_by = 'sql' WHERE id = $1", reversal);

        var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());

        Assert.Equal("LC001", error.SqlState);
        Assert.StartsWith("IDEMPOTENCY_CLAIM_REQUIRED:", error.MessageText, StringComparison.Ordinal);
        Assert.Equal(0, await ReversalsOfAsync(original));
    }
}
