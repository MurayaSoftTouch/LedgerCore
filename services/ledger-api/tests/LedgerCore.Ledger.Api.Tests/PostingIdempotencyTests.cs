using LedgerCore.Ledger.Api.Application;
using LedgerCore.Ledger.Api.Tests.Infrastructure;
using LedgerCore.Ledger.Domain;
using LedgerCore.Ledger.Domain.Journals;
using Npgsql;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>Idempotent posting (ADR-015) against real PostgreSQL.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PostingIdempotencyTests(PostgresFixture db)
{
    private async Task<long> CountAsync(string sql, Guid journal)
    {
        await using var connection = db.CreateRuntimeConnection();
        return await Sql.ScalarAsync<long>(connection, sql, journal);
    }

    private Task<long> PostingsAsync(Guid journal) =>
        CountAsync("SELECT count(*) FROM journal_status_transitions WHERE journal_id = $1 AND to_status = 'POSTED'", journal);

    private Task<long> EventsAsync(Guid journal) =>
        CountAsync("SELECT count(*) FROM outbox_events WHERE aggregate_id = $1 AND event_type = 'JournalPosted'", journal);

    private Task<long> ClaimsAsync(Guid journal) =>
        CountAsync("SELECT count(*) FROM command_idempotency WHERE target_journal_id = $1 AND operation = 'POST_JOURNAL'", journal);

    private Task<long> TransitionsAsync(Guid journal) =>
        CountAsync("SELECT count(*) FROM journal_status_transitions WHERE journal_id = $1", journal);

    private async Task AssertPostedOnceAsync(Guid journal)
    {
        Assert.Equal(1, await PostingsAsync(journal));
        Assert.Equal(1, await EventsAsync(journal));
        Assert.Equal(1, await ClaimsAsync(journal));
    }

    private async Task AssertUntouchedAsync(LedgerScenario s, Guid journal)
    {
        Assert.Equal(JournalStatus.Approved, (await s.GetAsync(journal)).Journal.Status);
        Assert.Equal(0, await PostingsAsync(journal));
        Assert.Equal(0, await EventsAsync(journal));
        Assert.Equal(0, await ClaimsAsync(journal));
    }

    [Fact]
    public async Task FirstRequestPostsAndRecordsTheClaimAndTheAudit()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 1500.25m), (s.Cash, Credit, 1500.25m));
        var key = LedgerScenario.NewKey();

        var result = await s.PostWithKeyAsync(id, key);

        Assert.False(result.Replayed);
        Assert.Equal(JournalStatus.Posted, result.Journal.Status);
        await AssertPostedOnceAsync(id);
        await using var connection = db.CreateRuntimeConnection();
        Assert.Equal(
            $"POST_JOURNAL|{key}|{id}|{id}|poster|{CommandFingerprint.ForPosting(s.LedgerId, id, "poster")}",
            await Sql.ScalarAsync<string>(
                connection,
                "SELECT operation || '|' || idempotency_key || '|' || target_journal_id || '|' || result_journal_id || '|' || requested_by || '|' || request_fingerprint FROM command_idempotency WHERE target_journal_id = $1",
                id));
        // The audit row names the command's key and the decision that authorized the posting.
        Assert.Equal(
            $"{key}|{(await s.GetAsync(id)).PolicyDecision!.DecisionId}",
            await Sql.ScalarAsync<string>(
                connection,
                "SELECT idempotency_key || '|' || policy_decision_id FROM journal_status_transitions WHERE journal_id = $1 AND to_status = 'POSTED'",
                id));
    }

    [Fact]
    public async Task IdenticalRetryReplaysTheOriginalAndWritesNothing()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var key = LedgerScenario.NewKey();
        var first = await s.PostWithKeyAsync(id, key);
        var transitions = await TransitionsAsync(id);

        var retry = await s.PostWithKeyAsync(id, key);

        Assert.True(retry.Replayed);
        Assert.Equal(first.Journal.Id, retry.Journal.Id);
        Assert.Equal(first.Journal.PostedAt, retry.Journal.PostedAt);
        Assert.Equal(first.Journal.PostedBy, retry.Journal.PostedBy);
        await AssertPostedOnceAsync(id);
        Assert.Equal(transitions, await TransitionsAsync(id));
    }

    [Fact]
    public async Task UnknownOutcomeIsRecoveredByRetryingWithTheSameKey()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var key = LedgerScenario.NewKey();

        // The posting commits, but the client never sees the response.
        _ = await s.PostWithKeyAsync(id, key);

        // The client does not know whether it worked, so it retries with the same key.
        var retry = await s.PostWithKeyAsync(id, key);

        Assert.True(retry.Replayed);
        Assert.Equal(JournalStatus.Posted, retry.Journal.Status);
        await AssertPostedOnceAsync(id);
    }

    [Fact]
    public async Task RetryWhileTheOriginalIsStillInFlightWaitsAndReplays()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var key = LedgerScenario.NewKey();
        var pause = new PausingCommitInterceptor();

        var original = s.PostWithKeyAsync(id, key, pause);
        await pause.Reached;
        var retry = s.PostWithKeyAsync(id, key);
        await Locks.WaitForBlockedBackendsAsync(db);
        pause.Release();

        Assert.False((await original).Replayed);
        Assert.True((await retry).Replayed);
        await AssertPostedOnceAsync(id);
    }

    [Fact]
    public async Task SameKeyForAnotherJournalIsAConflictAndChangesNothing()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var first = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var second = await s.ApprovedAsync((s.Rent, Debit, 20m), (s.Cash, Credit, 20m));
        var key = LedgerScenario.NewKey();
        await s.PostWithKeyAsync(first, key);

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostWithKeyAsync(second, key));

        Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
        await AssertUntouchedAsync(s, second);
        Assert.Equal(JournalStatus.Posted, (await s.PostWithKeyAsync(second, LedgerScenario.NewKey())).Journal.Status);
    }

    [Fact]
    public async Task SameKeyFromAnotherActorIsAConflict()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var key = LedgerScenario.NewKey();
        await s.PostWithKeyAsync(id, key, "poster");

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostWithKeyAsync(id, key, "someone-else"));

        Assert.Equal("IDEMPOTENCY_CONFLICT", error.Code);
        await AssertPostedOnceAsync(id);
    }

    [Fact]
    public async Task AnotherKeyCannotPostAPostedJournalAgain()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await s.PostWithKeyAsync(id, LedgerScenario.NewKey());

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostWithKeyAsync(id, LedgerScenario.NewKey()));

        Assert.Equal("JOURNAL_ALREADY_POSTED", error.Code);
        await AssertPostedOnceAsync(id);
    }

    [Fact]
    public async Task FailedPostingLeavesNoClaimSoTheKeyCanBeRetried()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await s.SubmitAsync(id);
        var key = LedgerScenario.NewKey();

        // Not approved yet: refused, and the key is not consumed.
        Assert.Equal("JOURNAL_INVALID_STATE", (await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostWithKeyAsync(id, key))).Code);
        Assert.Equal(0, await ClaimsAsync(id));

        await s.ApproveAsync(id);
        var result = await s.PostWithKeyAsync(id, key);

        Assert.False(result.Replayed);
        await AssertPostedOnceAsync(id);
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsPostOnceAndAllReturnTheSameResult()
    {
        const int callers = 8;
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 42m), (s.Cash, Credit, 42m));
        var key = LedgerScenario.NewKey();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, callers).Select(async _ =>
        {
            await start.Task;
            return await s.PostWithKeyAsync(id, key);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, r => !r.Replayed);
        Assert.All(results, r => Assert.Equal(JournalStatus.Posted, r.Journal.Status));
        Assert.Single(results.Select(r => r.Journal.PostedAt).Distinct());
        await AssertPostedOnceAsync(id);
    }

    [Fact]
    public async Task ConcurrentRequestsWithDifferentKeysStillPostOnce()
    {
        const int callers = 8;
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 42m), (s.Cash, Credit, 42m));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, callers).Select(async _ =>
        {
            await start.Task;
            try
            {
                await s.PostWithKeyAsync(id, LedgerScenario.NewKey());
                return "posted";
            }
            catch (LedgerDomainException e)
            {
                return e.Code;
            }
        }).ToArray();
        start.SetResult();
        var outcomes = await Task.WhenAll(tasks);

        Assert.Single(outcomes, o => o == "posted");
        Assert.All(outcomes.Where(o => o != "posted"), o => Assert.Equal("JOURNAL_ALREADY_POSTED", o));
        await AssertPostedOnceAsync(id);
    }

    [Fact]
    public async Task ConcurrentUseOfOneKeyOnTwoJournalsIsDecidedByTheDatabase()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var first = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var second = await s.ApprovedAsync((s.Rent, Debit, 20m), (s.Cash, Credit, 20m));
        var key = LedgerScenario.NewKey();
        var pause = new PausingCommitInterceptor();

        // A claims the key for the first journal and pauses before COMMIT. B locks the second
        // journal (no conflict there), then waits on A's uncommitted claim of the same key.
        var a = s.PostWithKeyAsync(first, key, pause);
        await pause.Reached;
        var b = s.PostWithKeyAsync(second, key);
        await Locks.WaitForBlockedBackendsAsync(db);
        pause.Release();

        Assert.False((await a).Replayed);
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<LedgerDomainException>(() => b)).Code);
        await AssertPostedOnceAsync(first);
        await AssertUntouchedAsync(s, second);
    }

    [Theory]
    [InlineData("INSERT INTO command_idempotency")]
    [InlineData("UPDATE journals SET")]
    [InlineData("INSERT INTO outbox_events")]
    public async Task InjectedFailureAtAnyStatementLeavesNothingAndTheKeyStillWorks(string failingStatement)
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var key = LedgerScenario.NewKey();
        var failure = new FailingCommandInterceptor(failingStatement);
        var transitions = await TransitionsAsync(id);

        await Injected.FailureAsync(() => s.PostWithKeyAsync(id, key, failure));

        Assert.True(failure.Fired);
        await AssertUntouchedAsync(s, id);
        Assert.Equal(transitions, await TransitionsAsync(id));
        Assert.False((await s.PostWithKeyAsync(id, key)).Replayed);
        await AssertPostedOnceAsync(id);
    }

    [Fact]
    public async Task InjectedFailureAtCommitLeavesNothingAndTheKeyStillWorks()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var key = LedgerScenario.NewKey();

        await Injected.FailureAsync(() => s.PostWithKeyAsync(id, key, new FailingCommitInterceptor()));

        await AssertUntouchedAsync(s, id);
        Assert.False((await s.PostWithKeyAsync(id, key)).Replayed);
        await AssertPostedOnceAsync(id);
    }

    private static void AssertGuard(PostgresException error, string code)
    {
        Assert.Equal("LC001", error.SqlState);
        Assert.StartsWith(code + ":", error.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabaseRefusesAPostingWithoutAClaim()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        const string post = "UPDATE journals SET status = 'POSTED', posted_at = now(), posted_by = 'sql' WHERE id = $1";
        await using var runtime = db.CreateRuntimeConnection();
        await using var owner = db.CreateOwnerConnection();

        AssertGuard(await Sql.FailsAsync(runtime, post, id), "IDEMPOTENCY_CLAIM_REQUIRED");
        AssertGuard(await Sql.FailsAsync(owner, post, id), "IDEMPOTENCY_CLAIM_REQUIRED");
    }

    [Fact]
    public async Task DatabaseRefusesAClaimWhoseEffectDidNotHappen()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await using var runtime = db.CreateRuntimeConnection();

        // Autocommit: the deferred check runs at the end of the statement's transaction.
        var error = await Sql.FailsAsync(
            runtime,
            "INSERT INTO command_idempotency VALUES ($1, 'POST_JOURNAL', 'forged-key-1', repeat('a', 64), $2, $2, 'sql', NULL, now())",
            s.LedgerId,
            id);

        AssertGuard(error, "IDEMPOTENCY_EFFECT_MISSING");
        Assert.Equal(0, await ClaimsAsync(id));
    }

    [Fact]
    public async Task ClaimsMustNameJournalsOfTheirOwnLedger()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var other = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await using var runtime = db.CreateRuntimeConnection();

        var error = await Sql.FailsAsync(
            runtime,
            "INSERT INTO command_idempotency VALUES ($1, 'POST_JOURNAL', 'forged-key-2', repeat('a', 64), $2, $2, 'sql', NULL, now())",
            other.LedgerId,
            id);

        AssertGuard(error, "IDEMPOTENCY_LEDGER_MISMATCH");
    }

    [Fact]
    public async Task ClaimsAreAppendOnly()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await using var runtime = db.CreateRuntimeConnection();
        await using var owner = db.CreateOwnerConnection();

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Sql.FailsAsync(runtime, "UPDATE command_idempotency SET requested_by = 'x' WHERE target_journal_id = $1", id)).SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Sql.FailsAsync(runtime, "DELETE FROM command_idempotency WHERE target_journal_id = $1", id)).SqlState);
        AssertGuard(await Sql.FailsAsync(owner, "UPDATE command_idempotency SET requested_by = 'x' WHERE target_journal_id = $1", id), "IDEMPOTENCY_RECORD_IMMUTABLE");
        AssertGuard(await Sql.FailsAsync(owner, "DELETE FROM command_idempotency WHERE target_journal_id = $1", id), "IDEMPOTENCY_RECORD_IMMUTABLE");
        AssertGuard(await Sql.FailsAsync(owner, "TRUNCATE command_idempotency"), "LEDGER_HISTORY_IMMUTABLE");
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has spaces in it")]
    [InlineData("semi;colon-key")]
    public async Task DatabaseRefusesMalformedKeys(string key)
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await using var runtime = db.CreateRuntimeConnection();

        var error = await Sql.FailsAsync(
            runtime,
            "INSERT INTO command_idempotency VALUES ($1, 'POST_JOURNAL', $3, repeat('a', 64), $2, $2, 'sql', NULL, now())",
            s.LedgerId,
            id,
            key);

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_command_idempotency_key", error.ConstraintName);
    }
}
