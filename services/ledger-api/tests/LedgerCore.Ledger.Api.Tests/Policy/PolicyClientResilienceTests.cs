using System.Diagnostics;
using System.Net;
using LedgerCore.Ledger.Api.Integration.Correlation;
using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Tests.Infrastructure;
using static LedgerCore.Ledger.Api.Tests.Policy.PolicyClientHarness;

namespace LedgerCore.Ledger.Api.Tests.Policy;

/// <summary>
/// Retry, timeout and status mapping of the real client pipeline (no database). Runs outside the
/// parallel test pool because two tests assert wall-clock bounds.
/// </summary>
[Collection(TimingSensitive.Name)]
public sealed class PolicyClientResilienceTests
{
    private static readonly string Approved = ExampleResponse("response.approved.valid.json");

    private static async Task<(PolicyEvaluationResult Result, StubHttpHandler Handler)> Evaluate(
        Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond, int totalMs = 1_000, int attemptMs = 200, int retries = 2)
    {
        var handler = new StubHttpHandler(respond);
        var (client, provider) = Create(handler, totalMs, attemptMs, retries);
        await using (provider)
        {
            return (await client.EvaluateAsync(Request(), CancellationToken.None), handler);
        }
    }

    private static Task<HttpResponseMessage> Status(HttpStatusCode status, string body = "{}") =>
        Task.FromResult(StubHttpHandler.Json(status, body, "application/problem+json"));

    [Fact]
    public async Task SendsContractRequestWithCredentialAndCorrelationId()
    {
        CorrelationId.Current = "corr-client-test";
        var (result, handler) = await Evaluate((_, _) => Task.FromResult(StubHttpHandler.Json(HttpStatusCode.OK, Approved)));

        Assert.IsType<PolicyEvaluationResult.Decided>(result);
        var (request, body) = Assert.Single(handler.Received);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://policy.test:8081/v1/policy-decisions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
        Assert.Equal("corr-client-test", Assert.Single(request.Headers.GetValues(CorrelationId.Header)));
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.True(Contracts.IsValidRequest(body), body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task TransientStatusIsRetriedThenSucceeds(HttpStatusCode transient)
    {
        var (result, handler) = await Evaluate((_, attempt) =>
            attempt == 1 ? Status(transient) : Task.FromResult(StubHttpHandler.Json(HttpStatusCode.OK, Approved)));

        Assert.IsType<PolicyEvaluationResult.Decided>(result);
        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task PersistentUnavailabilityIsBoundedAndReportedAsUnavailable()
    {
        var (result, handler) = await Evaluate((_, _) => Status(HttpStatusCode.ServiceUnavailable));

        Assert.Equal(PolicyFailureKind.Unavailable, Assert.IsType<PolicyEvaluationResult.Failed>(result).Kind);
        Assert.Equal(3, handler.Attempts); // 1 + 2 retries
    }

    [Fact]
    public async Task ConnectionFailureIsRetriedThenReportedAsUnavailable()
    {
        var (result, handler) = await Evaluate((_, _) => throw new HttpRequestException("connection refused"));

        Assert.Equal(PolicyFailureKind.Unavailable, Assert.IsType<PolicyEvaluationResult.Failed>(result).Kind);
        Assert.Equal(3, handler.Attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, nameof(PolicyFailureKind.Unavailable))]
    [InlineData(HttpStatusCode.BadRequest, nameof(PolicyFailureKind.ContractViolation))]
    [InlineData(HttpStatusCode.Unauthorized, nameof(PolicyFailureKind.AuthenticationFailed))]
    [InlineData(HttpStatusCode.Forbidden, nameof(PolicyFailureKind.AuthenticationFailed))]
    [InlineData((HttpStatusCode)418, nameof(PolicyFailureKind.InvalidResponse))]
    public async Task NonTransientStatusIsNotRetried(HttpStatusCode status, string expectedKind)
    {
        var expected = Enum.Parse<PolicyFailureKind>(expectedKind);
        var (result, handler) = await Evaluate((_, _) => Status(status));

        Assert.Equal(expected, Assert.IsType<PolicyEvaluationResult.Failed>(result).Kind);
        Assert.Equal(1, handler.Attempts);
    }

    [Theory]
    [InlineData("IDEMPOTENCY_CONFLICT", nameof(PolicyFailureKind.Conflict))]
    [InlineData("CONTRACT_VERSION_UNSUPPORTED", nameof(PolicyFailureKind.ContractViolation))]
    public async Task ConflictsAreModelledAndNeverRetried(string code, string expectedKind)
    {
        var expected = Enum.Parse<PolicyFailureKind>(expectedKind);
        var (result, handler) = await Evaluate((_, _) => Status(HttpStatusCode.Conflict, $$"""{"type":"urn:ledgercore:problem:{{code}}","title":"{{code}}","status":409,"code":"{{code}}"}"""));

        Assert.Equal(expected, Assert.IsType<PolicyEvaluationResult.Failed>(result).Kind);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task RetriesResendTheSameTransaction()
    {
        var (result, handler) = await Evaluate((_, _) => Status(HttpStatusCode.ServiceUnavailable));

        Assert.IsType<PolicyEvaluationResult.Failed>(result);
        var bodies = handler.Received.Select(r => r.Body).ToArray();
        Assert.Equal(3, bodies.Length);
        // Same transactionId and fingerprint, so the policy service's idempotency returns one decision.
        Assert.Single(bodies.Distinct());
        Assert.Contains($"\"transactionId\":\"{TransactionId}\"", bodies[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("REVIEW_REQUIRED", nameof(PolicyDecisionValue.ReviewRequired))]
    [InlineData("REJECTED", nameof(PolicyDecisionValue.Rejected))]
    public async Task BusinessDecisionsAreNeverRetried(string decision, string expected)
    {
        var body = ExampleResponse("response.review-required.valid.json").Replace("\"REVIEW_REQUIRED\"", $"\"{decision}\"", StringComparison.Ordinal);
        var (result, handler) = await Evaluate((_, _) => Task.FromResult(StubHttpHandler.Json(HttpStatusCode.OK, body)));

        Assert.Equal(Enum.Parse<PolicyDecisionValue>(expected), Assert.IsType<PolicyEvaluationResult.Decided>(result).Decision.Decision);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task ContractViolatingDecisionIsNotRetried()
    {
        var (result, handler) = await Evaluate((_, _) =>
            Task.FromResult(StubHttpHandler.Json(HttpStatusCode.OK, ExampleResponse("response.rejected-without-reason.invalid.json"))));

        Assert.Equal(PolicyFailureKind.ContractViolation, Assert.IsType<PolicyEvaluationResult.Failed>(result).Kind);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task SlowAttemptsAreCutOffRetriedAndReportedAsTimeout()
    {
        var stopwatch = Stopwatch.StartNew();
        var (result, handler) = await Evaluate(
            async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                return StubHttpHandler.Json(HttpStatusCode.OK, Approved);
            },
            totalMs: 2_000,
            attemptMs: 150);

        Assert.Equal(PolicyFailureKind.Timeout, Assert.IsType<PolicyEvaluationResult.Failed>(result).Kind);
        Assert.Equal(3, handler.Attempts);
        // Every attempt would take 10 s; finishing near the 2 s budget proves the cut-off.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task TotalBudgetBoundsEverythingIncludingBackoff()
    {
        var stopwatch = Stopwatch.StartNew();
        var (result, _) = await Evaluate(
            async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                return StubHttpHandler.Json(HttpStatusCode.OK, Approved);
            },
            totalMs: 300,
            attemptMs: 5_000,
            retries: 5);

        Assert.Equal(PolicyFailureKind.Timeout, Assert.IsType<PolicyEvaluationResult.Failed>(result).Kind);
        // A 10 s attempt with 5 retries would take a minute; the 300 ms total budget bounds it.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task MalformedBodyIsInvalidResponseAndNotRetried()
    {
        var (result, handler) = await Evaluate((_, _) => Task.FromResult(StubHttpHandler.Json(HttpStatusCode.OK, "<html>")));

        Assert.Equal(PolicyFailureKind.InvalidResponse, Assert.IsType<PolicyEvaluationResult.Failed>(result).Kind);
        Assert.Equal(1, handler.Attempts);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitive
{
    public const string Name = "timing-sensitive";
}
