using LedgerCore.Ledger.Api.Tests.Infrastructure;
using LedgerCore.Ledger.Domain;
using LedgerCore.Ledger.Domain.Journals;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Api.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class ReversalTests(PostgresFixture db)
{
    [Fact]
    public async Task ReversalCreatesMirroredJournalAndPostsUnderSameInvariants()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 700m), (s.Payables, Debit, 50.25m), (s.Cash, Credit, 750.25m));

        var reversal = await s.ReverseAsync(original);

        Assert.Equal(JournalStatus.PendingApproval, reversal.Status);
        Assert.Equal(original, reversal.ReversesJournalId);
        var stored = (await s.GetAsync(reversal.Id)).Journal;
        var source = (await s.GetAsync(original)).Journal;
        Assert.Equal(
            source.Entries.OrderBy(e => e.LineNumber).Select(e => (e.AccountId, e.Direction.Opposite(), e.Amount)),
            stored.Entries.OrderBy(e => e.LineNumber).Select(e => (e.AccountId, e.Direction, e.Amount)));
        Assert.True(DoubleEntry.Totals(stored.Entries).IsBalanced);

        await s.ApproveAsync(reversal.Id);
        Assert.Equal(JournalStatus.Posted, (await s.PostAsync(reversal.Id)).Status);
    }

    [Fact]
    public async Task OriginalRowIsUnchangedByReversal()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var before = await SnapshotAsync(original);

        var reversal = await s.ReverseAsync(original);
        await s.ApproveAsync(reversal.Id);
        await s.PostAsync(reversal.Id);

        Assert.Equal(before, await SnapshotAsync(original));
    }

    [Fact]
    public async Task ReversedStateIsDerivedFromPostedReversalOnly()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));

        var reversal = await s.ReverseAsync(original);
        Assert.Null((await s.GetAsync(original)).ReversedByJournalId);

        await s.ApproveAsync(reversal.Id);
        await s.PostAsync(reversal.Id);
        var view = await s.GetAsync(original);
        Assert.Equal(reversal.Id, view.ReversedByJournalId);
        Assert.Equal(JournalStatus.Posted, view.Journal.Status);
    }

    [Fact]
    public async Task SecondReversalIsRejected()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await s.ReverseAsync(original);

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.ReverseAsync(original));

        Assert.Equal("JOURNAL_ALREADY_REVERSED", error.Code);
        Assert.Equal(1, await CountReversalsAsync(original));
    }

    [Fact]
    public async Task RejectedReversalAllowsANewReversal()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var rejected = await s.ReverseAsync(original);
        await s.RejectAsync(rejected.Id);

        var second = await s.ReverseAsync(original);

        Assert.NotEqual(rejected.Id, second.Id);
        Assert.Equal(2, await CountReversalsAsync(original));
    }

    [Fact]
    public async Task ReversalOfReversalIsRejected()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var reversal = await s.ReverseAsync(original);
        await s.ApproveAsync(reversal.Id);
        await s.PostAsync(reversal.Id);

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.ReverseAsync(reversal.Id));

        Assert.Equal("REVERSAL_OF_REVERSAL", error.Code);
    }

    [Fact]
    public async Task UnpostedJournalCannotBeReversed()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var approved = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.ReverseAsync(approved));

        Assert.Equal("JOURNAL_NOT_POSTED", error.Code);
    }

    [Fact]
    public async Task ConcurrentReversalRequestsCreateExactlyOneReversal()
    {
        const int attempts = 8;
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 99.99m), (s.Cash, Credit, 99.99m));

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, attempts).Select(async _ =>
        {
            await start.Task;
            try
            {
                await s.ReverseAsync(original);
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
        Assert.Equal(1, await CountReversalsAsync(original));
    }

    [Fact]
    public async Task InjectedFailureDuringReversalLeavesNoReversal()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.ReverseAsync(original, new FailingCommitInterceptor()));

        Assert.Equal(0, await CountReversalsAsync(original));
        await using var connection = db.CreateRuntimeConnection();
        Assert.Equal(0L, await Sql.ScalarAsync<long>(
            connection,
            "SELECT count(*) FROM journal_entries e JOIN journals j ON j.id = e.journal_id WHERE j.reverses_journal_id = $1",
            original));
        Assert.Equal(JournalStatus.PendingApproval, (await s.ReverseAsync(original)).Status);
    }

    private async Task<long> CountReversalsAsync(Guid original)
    {
        await using var connection = db.CreateRuntimeConnection();
        return await Sql.ScalarAsync<long>(connection, "SELECT count(*) FROM journals WHERE reverses_journal_id = $1", original);
    }

    /// <summary>The full stored representation of a journal and its entries.</summary>
    private async Task<string?> SnapshotAsync(Guid journalId)
    {
        await using var connection = db.CreateRuntimeConnection();
        return await Sql.ScalarAsync<string>(
            connection,
            "SELECT (SELECT row_to_json(j)::text FROM journals j WHERE j.id = $1) || " +
            "(SELECT json_agg(e ORDER BY e.line_number)::text FROM journal_entries e WHERE e.journal_id = $1)",
            journalId);
    }
}
