using System.Net;
using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Tests.Infrastructure;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>The HTTP contract of idempotent posting and reversal (ADR-015).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class IdempotencyApiTests(PostgresFixture db) : IDisposable
{
    private readonly LedgerApiFactory _factory = new(db);

    public void Dispose() => _factory.Dispose();

    private async Task<(ApiClient Api, Guid Ledger, Guid Journal)> ApprovedAsync()
    {
        var api = ApiClient.Create(_factory);
        var ledger = await api.CreateLedgerAsync();
        var rent = await api.OpenAccountAsync(ledger, "5000", "EXPENSE");
        var cash = await api.OpenAccountAsync(ledger, "1000", "ASSET");
        var journal = await api.CreateJournalAsync(ledger);
        await api.AddEntryAsync(ledger, journal, rent, "DEBIT", "250.00");
        await api.AddEntryAsync(ledger, journal, cash, "CREDIT", "250.00");
        await api.CommandAsync(ledger, journal, "submit");
        await api.ApproveAsync(ledger, journal);
        return (api, ledger, journal);
    }

    [Theory]
    [InlineData("post")]
    [InlineData("reverse")]
    public async Task TheKeyIsRequired(string command)
    {
        var (api, ledger, journal) = await ApprovedAsync();

        await ApiClient.ExpectProblemAsync(await api.CommandAsync(ledger, journal, command, idempotencyKey: string.Empty), HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_REQUIRED");
    }

    [Theory]
    [InlineData("short")]
    [InlineData("contains spaces")]
    [InlineData("quote\"inside-key")]
    [InlineData("ünïcode-key-value")]
    public async Task MalformedKeysAreRejected(string key)
    {
        var (api, ledger, journal) = await ApprovedAsync();

        await ApiClient.ExpectProblemAsync(await api.CommandAsync(ledger, journal, "post", key), HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_INVALID");
    }

    [Fact]
    public async Task OversizedKeysAreRejectedAndTheLongestAllowedWorks()
    {
        var (api, ledger, journal) = await ApprovedAsync();

        await ApiClient.ExpectProblemAsync(await api.CommandAsync(ledger, journal, "post", new string('k', 129)), HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_INVALID");
        await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "post", new string('k', 128)), HttpStatusCode.OK);
    }

    [Fact]
    public async Task AReplayReturnsTheSameBodyAndSaysItIsAReplay()
    {
        var (api, ledger, journal) = await ApprovedAsync();
        var key = Guid.NewGuid().ToString();

        var first = await api.CommandAsync(ledger, journal, "post", key);
        var replay = await api.CommandAsync(ledger, journal, "post", key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        var body = await ApiClient.ExpectAsync(replay, HttpStatusCode.OK);
        Assert.Equal("POSTED", body["status"]!.GetValue<string>());
        Assert.Equal("APPROVED", body["policyDecision"]!["decision"]!.GetValue<string>());
    }

    [Fact]
    public async Task AReplayDoesNotDependOnTheCorrelationId()
    {
        var (api, ledger, journal) = await ApprovedAsync();
        var key = Guid.NewGuid().ToString();
        await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "post", key), HttpStatusCode.OK);

        api.Http.DefaultRequestHeaders.Add("X-Correlation-Id", "a-different-trace");
        var replay = await api.CommandAsync(ledger, journal, "post", key);

        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
    }

    [Fact]
    public async Task AConflictingReuseIsA409()
    {
        var (api, ledger, journal) = await ApprovedAsync();
        var (_, _, other) = await ApprovedAsyncIn(api, ledger);
        var key = Guid.NewGuid().ToString();
        await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "post", key), HttpStatusCode.OK);

        await ApiClient.ExpectProblemAsync(await api.CommandAsync(ledger, other, "post", key), HttpStatusCode.Conflict, "IDEMPOTENCY_CONFLICT");
        Assert.Equal("APPROVED", (await api.GetJournalAsync(ledger, other))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task AReversalReplayIs201WithTheSameReversal()
    {
        var (api, ledger, journal) = await ApprovedAsync();
        await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "post"), HttpStatusCode.OK);
        var key = Guid.NewGuid().ToString();

        var first = await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "reverse", key, new { description = "wrong period" }), HttpStatusCode.Created);
        var replay = await api.CommandAsync(ledger, journal, "reverse", key, new { description = "wrong period" });
        var body = await ApiClient.ExpectAsync(replay, HttpStatusCode.Created);

        Assert.Equal("true", Assert.Single(replay.Headers.GetValues("Idempotency-Replayed")));
        Assert.Equal(first["id"]!.GetValue<Guid>(), body["id"]!.GetValue<Guid>());
        await ApiClient.ExpectProblemAsync(
            await api.CommandAsync(ledger, journal, "reverse", key, new { description = "another reason" }), HttpStatusCode.Conflict, "IDEMPOTENCY_CONFLICT");
    }

    [Fact]
    public async Task AReplayedReversalObtainsApprovalOnceThePolicyServiceIsBack()
    {
        var (api, ledger, journal) = await ApprovedAsync();
        await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "post"), HttpStatusCode.OK);
        var key = Guid.NewGuid().ToString();

        // The policy service is down for the reversal: it is created and stays PENDING_APPROVAL.
        var created = await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "reverse", key), HttpStatusCode.Created);
        var reversal = created["id"]!.GetValue<Guid>();
        Assert.Equal("PENDING_APPROVAL", created["status"]!.GetValue<string>());
        Assert.Equal("UNAVAILABLE", created["approvalFailure"]!.GetValue<string>());

        // The client retries the same request; policy now answers.
        _factory.Policy.Decide(reversal, PolicyDecisionValue.Approved);
        var replayed = await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "reverse", key), HttpStatusCode.Created);

        Assert.Equal(reversal, replayed["id"]!.GetValue<Guid>());
        Assert.Equal("APPROVED", replayed["status"]!.GetValue<string>());
        // Asked once when created (unavailable) and once on the replay; never again after the decision.
        Assert.Equal(2, _factory.Policy.CallsFor(reversal));
        await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "reverse", key), HttpStatusCode.Created);
        Assert.Equal(2, _factory.Policy.CallsFor(reversal));
    }

    [Fact]
    public async Task PostingNeverCallsThePolicyService()
    {
        var (api, ledger, journal) = await ApprovedAsync();
        var callsBefore = _factory.Policy.Requests.Count;
        _factory.Policy.Fail(journal, PolicyFailureKind.Unavailable);

        await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "post"), HttpStatusCode.OK);

        Assert.Equal(callsBefore, _factory.Policy.Requests.Count);
    }

    private static async Task<(ApiClient Api, Guid Ledger, Guid Journal)> ApprovedAsyncIn(ApiClient api, Guid ledger)
    {
        var accounts = await ApiClient.ExpectAsync(await api.Http.GetAsync(new Uri($"/api/v1/ledgers/{ledger}/accounts", UriKind.Relative)), HttpStatusCode.OK);
        var ids = accounts.AsArray().ToDictionary(a => a!["code"]!.GetValue<string>(), a => a!["id"]!.GetValue<Guid>());
        var journal = await api.CreateJournalAsync(ledger);
        await api.AddEntryAsync(ledger, journal, ids["5000"], "DEBIT", "75.00");
        await api.AddEntryAsync(ledger, journal, ids["1000"], "CREDIT", "75.00");
        await api.CommandAsync(ledger, journal, "submit");
        await api.ApproveAsync(ledger, journal);
        return (api, ledger, journal);
    }
}
