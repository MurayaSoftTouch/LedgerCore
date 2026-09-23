using System.Net;
using System.Net.Http.Json;
using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Tests.Infrastructure;

namespace LedgerCore.Ledger.Api.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class ApprovalApiTests(PostgresFixture db) : IDisposable
{
    private readonly LedgerApiFactory _factory = new(db);

    private async Task<(ApiClient Api, Guid Ledger, Guid Journal)> DraftAsync(string type = "PAYMENT")
    {
        var api = ApiClient.Create(_factory);
        var ledger = await api.CreateLedgerAsync();
        var rent = await api.OpenAccountAsync(ledger, "5000", "EXPENSE");
        var cash = await api.OpenAccountAsync(ledger, "1000", "ASSET");
        var journal = await api.CreateJournalAsync(ledger, transactionType: type);
        await api.AddEntryAsync(ledger, journal, rent, "DEBIT", "250.00");
        await api.AddEntryAsync(ledger, journal, cash, "CREDIT", "250.00");
        return (api, ledger, journal);
    }

    [Fact]
    public async Task SubmitRequestsApprovalAndReturnsTheDecision()
    {
        var (api, ledger, journal) = await DraftAsync();
        _factory.Policy.Decide(journal, PolicyDecisionValue.Approved);

        var submitted = await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "submit"), HttpStatusCode.OK);

        Assert.Equal("APPROVED", submitted["status"]!.GetValue<string>());
        Assert.Equal("APPROVED", submitted["policyDecision"]!["decision"]!.GetValue<string>());
        Assert.Equal("stub-policy@1", submitted["policyDecision"]!["policyVersion"]!.GetValue<string>());
        Assert.Null(submitted["approvalFailure"]);
        await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "post"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task SubmitWhilePolicyIsDownKeepsTheSubmissionAndReportsTheFailure()
    {
        var (api, ledger, journal) = await DraftAsync();
        _factory.Policy.Fail(journal, PolicyFailureKind.Unavailable);

        var submitted = await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "submit"), HttpStatusCode.OK);

        Assert.Equal("PENDING_APPROVAL", submitted["status"]!.GetValue<string>());
        Assert.Equal("UNAVAILABLE", submitted["approvalFailure"]!.GetValue<string>());
        Assert.Null(submitted["policyDecision"]);
    }

    [Theory]
    [InlineData(nameof(PolicyFailureKind.Timeout), HttpStatusCode.ServiceUnavailable, "POLICY_TIMEOUT")]
    [InlineData(nameof(PolicyFailureKind.Unavailable), HttpStatusCode.ServiceUnavailable, "POLICY_UNAVAILABLE")]
    [InlineData(nameof(PolicyFailureKind.ContractViolation), HttpStatusCode.BadGateway, "POLICY_CONTRACT_VIOLATION")]
    [InlineData(nameof(PolicyFailureKind.InvalidResponse), HttpStatusCode.BadGateway, "POLICY_INVALID_RESPONSE")]
    [InlineData(nameof(PolicyFailureKind.AuthenticationFailed), HttpStatusCode.BadGateway, "POLICY_AUTHENTICATION_FAILED")]
    [InlineData(nameof(PolicyFailureKind.Conflict), HttpStatusCode.Conflict, "POLICY_CONFLICT")]
    public async Task RequestApprovalFailuresAreProblemsAndChangeNothing(string kind, HttpStatusCode status, string code)
    {
        var (api, ledger, journal) = await DraftAsync();
        await api.CommandAsync(ledger, journal, "submit");
        _factory.Policy.Fail(journal, Enum.Parse<PolicyFailureKind>(kind));

        await ApiClient.ExpectProblemAsync(await api.CommandAsync(ledger, journal, "request-approval"), status, code);

        Assert.Equal("PENDING_APPROVAL", (await api.GetJournalAsync(ledger, journal))["status"]!.GetValue<string>());
        await ApiClient.ExpectProblemAsync(await api.CommandAsync(ledger, journal, "post"), HttpStatusCode.Conflict, "JOURNAL_INVALID_STATE");
    }

    [Fact]
    public async Task ReviewRequiredIsExposedAsManualReview()
    {
        var (api, ledger, journal) = await DraftAsync();
        _factory.Policy.Decide(journal, PolicyDecisionValue.ReviewRequired, "AMOUNT_EXCEEDS_REVIEW_THRESHOLD");

        var submitted = await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "submit"), HttpStatusCode.OK);

        Assert.Equal("PENDING_APPROVAL", submitted["status"]!.GetValue<string>());
        Assert.True(submitted["manualReviewRequired"]!.GetValue<bool>());
        Assert.Equal("REVIEW_REQUIRED", submitted["policyDecision"]!["decision"]!.GetValue<string>());
        var fetched = await api.GetJournalAsync(ledger, journal);
        Assert.True(fetched["manualReviewRequired"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RejectedJournalCannotBePosted()
    {
        var (api, ledger, journal) = await DraftAsync();
        _factory.Policy.Decide(journal, PolicyDecisionValue.Rejected, "AMOUNT_EXCEEDS_HARD_LIMIT");

        var submitted = await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "submit"), HttpStatusCode.OK);

        Assert.Equal("REJECTED", submitted["status"]!.GetValue<string>());
        await ApiClient.ExpectProblemAsync(await api.CommandAsync(ledger, journal, "post"), HttpStatusCode.Conflict, "JOURNAL_INVALID_STATE");
    }

    [Fact]
    public async Task TransactionTypeIsRequiredAndReversalIsReserved()
    {
        var api = ApiClient.Create(_factory);
        var ledger = await api.CreateLedgerAsync();

        await ApiClient.ExpectProblemAsync(
            await api.Http.PostAsJsonAsync($"/api/v1/ledgers/{ledger}/journals", new { currency = "KES", description = "x" }),
            HttpStatusCode.BadRequest,
            "TRANSACTION_TYPE_REQUIRED");
        await ApiClient.ExpectProblemAsync(
            await api.Http.PostAsJsonAsync($"/api/v1/ledgers/{ledger}/journals", new { currency = "KES", description = "x", transactionType = "REVERSAL" }),
            HttpStatusCode.BadRequest,
            "JOURNAL_TYPE_RESERVED");
    }

    [Fact]
    public async Task CorrelationIdIsEchoedAndStoredWithTheDecision()
    {
        var (api, ledger, journal) = await DraftAsync();
        _factory.Policy.Decide(journal, PolicyDecisionValue.Approved);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/ledgers/{ledger}/journals/{journal}/submit");
        request.Headers.Add("X-Correlation-Id", "corr-approval-42");

        var response = await api.Http.SendAsync(request);

        await ApiClient.ExpectAsync(response, HttpStatusCode.OK);
        Assert.Equal("corr-approval-42", Assert.Single(response.Headers.GetValues("X-Correlation-Id")));
        await using var connection = db.CreateRuntimeConnection();
        Assert.Equal("corr-approval-42", await Sql.ScalarAsync<string>(
            connection, "SELECT correlation_id FROM journal_policy_decisions WHERE journal_id = $1", journal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("has spaces and <script>")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task MissingOrUnsafeCorrelationIdsAreReplaced(string? supplied)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        if (supplied is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", supplied);
        }

        var response = await client.SendAsync(request);

        var echoed = Assert.Single(response.Headers.GetValues("X-Correlation-Id"));
        Assert.True(Guid.TryParse(echoed, out _), echoed);
    }

    [Fact]
    public async Task PolicyOutageDegradesDependenciesButNotReadiness()
    {
        // The test host points at a policy service that is not running.
        using var client = _factory.CreateClient();

        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var dependencies = await client.GetAsync(new Uri("/health/dependencies", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
        Assert.Equal("Degraded", await dependencies.Content.ReadAsStringAsync());
    }

    public void Dispose() => _factory.Dispose();
}
