using LedgerCore.Ledger.Api.Tests.Infrastructure;
using LedgerCore.Ledger.Domain;
using LedgerCore.Ledger.Domain.Journals;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Api.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class PostingTests(PostgresFixture db)
{
    [Fact]
    public async Task PostsApprovedJournalAndAuditsEveryTransition()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 1500.25m), (s.Cash, Credit, 1500.25m));

        var posted = await s.PostAsync(id);

        Assert.Equal(JournalStatus.Posted, posted.Status);
        Assert.Equal("poster", posted.PostedBy);
        await using var connection = db.CreateRuntimeConnection();
        var transitions = await Sql.ScalarAsync<string>(
            connection,
            "SELECT string_agg(coalesce(from_status, '∅') || '>' || to_status || '@' || actor, ' ' ORDER BY id) FROM journal_status_transitions WHERE journal_id = $1",
            id);
        Assert.Equal("∅>DRAFT@tester DRAFT>PENDING_APPROVAL@submitter PENDING_APPROVAL>APPROVED@policy-service APPROVED>POSTED@poster", transitions);
    }

    [Fact]
    public async Task CannotPostWithoutApproval()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await s.SubmitAsync(id);

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostAsync(id));

        Assert.Equal("JOURNAL_INVALID_STATE", error.Code);
        Assert.Equal(JournalStatus.PendingApproval, (await s.GetAsync(id)).Journal.Status);
    }

    [Fact]
    public async Task UnbalancedJournalNeverLeavesDraft()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync((s.Rent, Debit, 100.00m), (s.Cash, Credit, 99.99m));

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.SubmitAsync(id));

        Assert.Equal("JOURNAL_UNBALANCED", error.Code);
        Assert.Equal(JournalStatus.Draft, (await s.GetAsync(id)).Journal.Status);
    }

    [Fact]
    public async Task InactiveAccountBlocksPostingEvenAfterApproval()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await s.DeactivateAsync(s.Rent);

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostAsync(id));

        Assert.Equal("ACCOUNT_INACTIVE", error.Code);
        Assert.Equal(JournalStatus.Approved, (await s.GetAsync(id)).Journal.Status);
    }

    [Fact]
    public async Task PostingTwiceIsRejectedAndCreatesNoSecondEvent()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var firstPostedAt = (await s.GetAsync(id)).Journal.PostedAt;

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostAsync(id));

        Assert.Equal("JOURNAL_ALREADY_POSTED", error.Code);
        Assert.Equal(firstPostedAt, (await s.GetAsync(id)).Journal.PostedAt);
        Assert.Equal(1, await CountPostingsAsync(id));
    }

    [Fact]
    public async Task InjectedFailureBeforeCommitLeavesNoPartialState()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.PostAsync(id, new FailingCommitInterceptor()));

        var journal = (await s.GetAsync(id)).Journal;
        Assert.Equal(JournalStatus.Approved, journal.Status);
        Assert.Null(journal.PostedAt);
        Assert.Equal(0, await CountPostingsAsync(id));

        // The journal is still postable: the failed attempt left nothing behind.
        Assert.Equal(JournalStatus.Posted, (await s.PostAsync(id)).Status);
        Assert.Equal(1, await CountPostingsAsync(id));
    }

    [Fact]
    public async Task ConcurrentPostingAttemptsProduceExactlyOnePosting()
    {
        const int attempts = 8;
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 42m), (s.Cash, Credit, 42m));

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, attempts).Select(async _ =>
        {
            await start.Task;
            try
            {
                await s.PostAsync(id);
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
        Assert.Equal(1, await CountPostingsAsync(id));
        Assert.Equal(JournalStatus.Posted, (await s.GetAsync(id)).Journal.Status);
    }

    [Fact]
    public async Task SecondPosterBlocksOnRowLockThenObservesPostedState()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 5m), (s.Cash, Credit, 5m));
        var pause = new PausingCommitInterceptor();

        // A: runs the whole posting transaction, then pauses before COMMIT while holding the row lock.
        var first = s.PostAsync(id, pause);
        await pause.Reached;

        // B: must block on A's lock, not read a stale APPROVED row.
        var second = s.PostAsync(id);
        await Locks.WaitForBlockedBackendsAsync(db);
        Assert.False(second.IsCompleted);

        pause.Release();
        Assert.Equal(JournalStatus.Posted, (await first).Status);
        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => second);
        Assert.Equal("JOURNAL_ALREADY_POSTED", error.Code);
        Assert.Equal(1, await CountPostingsAsync(id));
    }

    [Fact]
    public async Task DeactivationWaitsForInFlightPosting()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 5m), (s.Cash, Credit, 5m));
        var pause = new PausingCommitInterceptor();

        var posting = s.PostAsync(id, pause);
        await pause.Reached;
        var deactivation = s.DeactivateAsync(s.Rent);
        await Locks.WaitForBlockedBackendsAsync(db);
        Assert.False(deactivation.IsCompleted);

        pause.Release();
        await posting;
        await deactivation;

        // Posting validated an active account and committed first; deactivation followed.
        Assert.Equal(JournalStatus.Posted, (await s.GetAsync(id)).Journal.Status);
    }

    private async Task<long> CountPostingsAsync(Guid journalId)
    {
        await using var connection = db.CreateRuntimeConnection();
        return await Sql.ScalarAsync<long>(
            connection, "SELECT count(*) FROM journal_status_transitions WHERE journal_id = $1 AND to_status = 'POSTED'", journalId);
    }
}
