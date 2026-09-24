using System.Text.Json;
using LedgerCore.Ledger.Api.Tests.Infrastructure;
using LedgerCore.Ledger.Domain;
using Npgsql;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>The JournalPosted outbox event is written atomically with the posting (ADR-014).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class OutboxTests(PostgresFixture db)
{
    private async Task<long> EventsForAsync(Guid journal)
    {
        await using var connection = db.CreateRuntimeConnection();
        return await Sql.ScalarAsync<long>(connection, "SELECT count(*) FROM outbox_events WHERE aggregate_id = $1", journal);
    }

    [Fact]
    public async Task PostingWritesExactlyOneJournalPostedEvent()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.PostedAsync((s.Rent, Debit, 1500.25m), (s.Cash, Credit, 1500.25m));

        await using var connection = db.CreateRuntimeConnection();
        var payload = await Sql.ScalarAsync<string>(
            connection, "SELECT payload::text FROM outbox_events WHERE aggregate_id = $1 AND event_type = 'JournalPosted' AND published_at IS NULL", id);
        using var json = JsonDocument.Parse(payload!);
        var root = json.RootElement;
        Assert.Equal(id, root.GetProperty("journalId").GetGuid());
        Assert.Equal("1500.25", root.GetProperty("totalAmount").GetString());
        Assert.Equal("PAYMENT", root.GetProperty("transactionType").GetString());
        Assert.Equal((await s.GetAsync(id)).PolicyDecision!.DecisionId, root.GetProperty("policyDecisionId").GetGuid());
        Assert.Equal(1, await EventsForAsync(id));
    }

    [Fact]
    public async Task NothingElseWritesEvents()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var approved = await s.ApprovedAsync((s.Rent, Debit, 5m), (s.Cash, Credit, 5m));

        Assert.Equal(0, await EventsForAsync(approved));
    }

    [Fact]
    public async Task FailedPostingCommitLeavesNoEvent()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 5m), (s.Cash, Credit, 5m));

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.PostAsync(id, new FailingCommitInterceptor()));

        Assert.Equal(0, await EventsForAsync(id));
        await s.PostAsync(id);
        Assert.Equal(1, await EventsForAsync(id));
    }

    [Fact]
    public async Task ConcurrentPostingsWriteOneEvent()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 5m), (s.Cash, Credit, 5m));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            await start.Task;
            try
            {
                await s.PostAsync(id);
            }
            catch (LedgerDomainException)
            {
            }
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(attempts);

        Assert.Equal(1, await EventsForAsync(id));
    }

    [Fact]
    public async Task ReversalPostingEventReferencesTheOriginal()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 5m), (s.Cash, Credit, 5m));
        var reversal = await s.ReverseAsync(original);
        await s.ApproveAsync(reversal.Id);
        await s.PostAsync(reversal.Id);

        await using var connection = db.CreateRuntimeConnection();
        var payload = await Sql.ScalarAsync<string>(connection, "SELECT payload::text FROM outbox_events WHERE aggregate_id = $1", reversal.Id);
        using var json = JsonDocument.Parse(payload!);
        Assert.Equal(original, json.RootElement.GetProperty("reversesJournalId").GetGuid());
        Assert.Equal("REVERSAL", json.RootElement.GetProperty("transactionType").GetString());
    }

    [Fact]
    public async Task EventsCanBeMarkedPublishedOnceAndOtherwiseNeverChange()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.PostedAsync((s.Rent, Debit, 5m), (s.Cash, Credit, 5m));
        await using var runtime = db.CreateRuntimeConnection();
        await using var owner = db.CreateOwnerConnection();

        Assert.Equal(1, await Sql.ExecuteAsync(runtime, "UPDATE outbox_events SET published_at = now() WHERE aggregate_id = $1", id));

        static void Guard(PostgresException e) => Assert.StartsWith("OUTBOX_IMMUTABLE:", e.MessageText, StringComparison.Ordinal);
        Guard(await Sql.FailsAsync(runtime, "UPDATE outbox_events SET published_at = now() WHERE aggregate_id = $1", id));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Sql.FailsAsync(runtime, "UPDATE outbox_events SET payload = '{}' WHERE aggregate_id = $1", id)).SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Sql.FailsAsync(runtime, "DELETE FROM outbox_events WHERE aggregate_id = $1", id)).SqlState);
        Guard(await Sql.FailsAsync(owner, "UPDATE outbox_events SET payload = '{}' WHERE aggregate_id = $1", id));
        Guard(await Sql.FailsAsync(owner, "DELETE FROM outbox_events WHERE aggregate_id = $1", id));
    }

    [Fact]
    public async Task JournalPostedEventForAnUnpostedJournalCannotCommit()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 5m), (s.Cash, Credit, 5m));
        await using var runtime = db.CreateRuntimeConnection();
        await runtime.OpenAsync();
        await using var tx = await runtime.BeginTransactionAsync();

        await Sql.ExecuteAsync(
            runtime,
            "INSERT INTO outbox_events (id, aggregate_type, aggregate_id, event_type, payload, created_at) VALUES (gen_random_uuid(), 'Journal', $1, 'JournalPosted', '{}', now())",
            id);

        var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.StartsWith("OUTBOX_AGGREGATE_NOT_POSTED:", error.MessageText, StringComparison.Ordinal);
    }
}
