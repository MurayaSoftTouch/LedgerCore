using System.Diagnostics;
using System.Net;
using System.Text;

namespace LedgerCore.IntegrationTests;

/// <summary>
/// Failure simulations against the real stack (Milestone 5): policy latency, PostgreSQL going away
/// under both services, and the ledger's HTTP limits on real Kestrel. Every test restores the stack.
/// </summary>
[Collection(StackGroup.Name)]
public sealed class FailureSimulationTests(Stack stack) : IAsyncLifetime
{
    public Task InitializeAsync() => stack.ResetProxyAsync();

    public Task DisposeAsync() => stack.ResetProxyAsync();

    [Fact]
    public async Task PolicyLatencyAboveTheAttemptTimeoutIsBoundedFailsClosedAndRecovers()
    {
        var s = await Scenario.CreateAsync(stack);
        var journal = await s.DraftAsync("100.00");
        // Every response arrives after 2.6 s; the stack's attempt timeout is 2 s within a 5 s budget.
        await stack.AddToxicAsync("slow", "latency", "downstream", new { latency = 2_600, jitter = 0 });

        var stopwatch = Stopwatch.StartNew();
        var submitted = await Scenario.Expect(await s.CommandAsync(journal, "submit"), HttpStatusCode.OK);
        stopwatch.Stop();

        Assert.Equal("PENDING_APPROVAL", submitted["status"]!.GetValue<string>());
        Assert.Equal("TIMEOUT", submitted["approvalFailure"]!.GetValue<string>());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), $"submit took {stopwatch.Elapsed}");
        await Scenario.ExpectProblem(await s.CommandAsync(journal, "post"), HttpStatusCode.Conflict, "JOURNAL_INVALID_STATE");

        await stack.RemoveToxicAsync("slow");
        var approved = await Scenario.Expect(await s.CommandAsync(journal, "request-approval"), HttpStatusCode.OK);

        // The slow attempts reached the policy service; idempotency kept it to one decision.
        Assert.Equal("APPROVED", approved["status"]!.GetValue<string>());
        Assert.Equal(1, await s.PolicyDecisionsAsync(journal));
        Assert.Equal(1, await s.LedgerEvidenceAsync(journal));
    }

    [Fact]
    public async Task APostgresOutageMakesBothServicesUnreadyButLiveAndTheyRecover()
    {
        var s = await Scenario.CreateAsync(stack);
        var journal = await s.DraftAsync("100.00");
        Assert.Equal("APPROVED", (await Scenario.Expect(await s.CommandAsync(journal, "submit"), HttpStatusCode.OK))["status"]!.GetValue<string>());

        await stack.StopPostgresAsync();
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await stack.Ledger.GetAsync(new Uri("health/live", UriKind.Relative))).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await stack.Ledger.GetAsync(new Uri("health/ready", UriKind.Relative))).StatusCode);
            var read = await stack.Ledger.GetAsync(new Uri($"api/v1/ledgers/{s.Ledger}/journals/{journal}", UriKind.Relative));
            await Scenario.ExpectProblem(read, HttpStatusCode.ServiceUnavailable, "LEDGER_DATABASE_UNAVAILABLE");
            Assert.Equal("1", Assert.Single(read.Headers.GetValues("Retry-After")));
            await Scenario.ExpectProblem(await s.CommandAsync(journal, "post"), HttpStatusCode.ServiceUnavailable, "LEDGER_DATABASE_UNAVAILABLE");

            Assert.Equal(HttpStatusCode.OK, (await stack.Policy.GetAsync(new Uri("actuator/health/liveness", UriKind.Relative))).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await stack.Policy.GetAsync(new Uri("actuator/health/readiness", UriKind.Relative))).StatusCode);
        }
        finally
        {
            await stack.StartPostgresAsync();
        }

        await EventuallyAsync(async () =>
            (await stack.Ledger.GetAsync(new Uri("health/ready", UriKind.Relative))).StatusCode == HttpStatusCode.OK
            && (await stack.Policy.GetAsync(new Uri("actuator/health/readiness", UriKind.Relative))).StatusCode == HttpStatusCode.OK);

        // After recovery: the approved journal posts from its durable evidence, and the ledger reconciles.
        await EventuallyAsync(async () => (await s.CommandAsync(journal, "post", idempotencyKey: "after-outage-" + journal)).StatusCode == HttpStatusCode.OK);
        Assert.Equal("POSTED", (await s.GetAsync(journal))["status"]!.GetValue<string>());
        var report = await Scenario.Expect(await stack.Ledger.GetAsync(new Uri($"api/v1/ledgers/{s.Ledger}/reconciliation", UriKind.Relative)), HttpStatusCode.OK);
        Assert.Equal("HEALTHY", report["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task RealKestrelBoundsBodiesAndSendsNoServerBanner()
    {
        var declared = await stack.Ledger.PostAsync(
            new Uri("api/v1/ledgers", UriKind.Relative),
            new StringContent(new string('x', 70_000), Encoding.UTF8, "application/json"));
        using var chunked = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("{\"code\":\"" + new string('x', 70_000) + "\"}")));
        chunked.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/ledgers") { Content = chunked };
        request.Headers.TransferEncodingChunked = true;
        var streamed = await stack.Ledger.SendAsync(request);
        var ok = await stack.Ledger.GetAsync(new Uri("health/live", UriKind.Relative));

        await Scenario.ExpectProblem(declared, HttpStatusCode.RequestEntityTooLarge, "REQUEST_TOO_LARGE");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);
        Assert.False(ok.Headers.Contains("Server"));
        Assert.False(declared.Headers.Contains("Server"));
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // still recovering
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the stack did not recover in time");
            }

            await Task.Delay(500);
        }
    }
}
