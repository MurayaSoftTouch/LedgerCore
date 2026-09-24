using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;

namespace LedgerCore.IntegrationTests;

/// <summary>Ledger ↔ policy through real services, real PostgreSQL and a controllable network.</summary>
[Collection(StackGroup.Name)]
public sealed class EndToEndTests(Stack stack) : IAsyncLifetime
{
    public Task InitializeAsync() => stack.ResetProxyAsync();

    public Task DisposeAsync() => stack.ResetProxyAsync();

    [Fact]
    public async Task ApprovedTransactionPostsWithPolicyEvidenceAndOutboxEvent()
    {
        var s = await Scenario.CreateAsync(stack);
        var journal = await s.DraftAsync("1500.00");

        var submitted = await Scenario.Expect(await s.CommandAsync(journal, "submit"), HttpStatusCode.OK);

        Assert.Equal("APPROVED", submitted["status"]!.GetValue<string>());
        Assert.Equal($"{s.PolicyKey}@1", submitted["policyDecision"]!["policyVersion"]!.GetValue<string>());
        var posted = await Scenario.Expect(await s.CommandAsync(journal, "post"), HttpStatusCode.OK);
        Assert.Equal("POSTED", posted["status"]!.GetValue<string>());
        Assert.Equal(1, await s.PolicyDecisionsAsync(journal));
        Assert.Equal(1, await s.ScalarAsync<long>("ledger", "SELECT count(*) FROM outbox_events WHERE aggregate_id = $1 AND event_type = 'JournalPosted'", journal));
        // The ledger's evidence is the policy service's decision.
        Assert.Equal(
            await s.ScalarAsync<Guid>("policy", "SELECT id FROM policy_decisions WHERE transaction_id = $1", journal),
            submitted["policyDecision"]!["decisionId"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task PostingIsIdempotentAndNeedsNoPolicyServiceOnceApproved()
    {
        var s = await Scenario.CreateAsync(stack);
        var journal = await s.DraftAsync("100.00");
        Assert.Equal("APPROVED", (await Scenario.Expect(await s.CommandAsync(journal, "submit"), HttpStatusCode.OK))["status"]!.GetValue<string>());
        var key = Guid.NewGuid().ToString();

        // The approval evidence is already persisted: posting works with the policy service unreachable.
        await stack.SetProxyAsync(new { enabled = false });
        var posted = await s.CommandAsync(journal, "post", idempotencyKey: key);
        var replayed = await s.CommandAsync(journal, "post", idempotencyKey: key);

        Assert.Equal("POSTED", (await Scenario.Expect(posted, HttpStatusCode.OK))["status"]!.GetValue<string>());
        Assert.Equal("true", Assert.Single(replayed.Headers.GetValues("Idempotency-Replayed")));
        Assert.Equal(await posted.Content.ReadAsStringAsync(), await replayed.Content.ReadAsStringAsync());
        Assert.Equal(1, await s.ScalarAsync<long>("ledger", "SELECT count(*) FROM outbox_events WHERE aggregate_id = $1 AND event_type = 'JournalPosted'", journal));
        Assert.Equal(1, await s.TransitionsAsync(journal, "POSTED"));
        Assert.Equal(key, await s.ScalarAsync<string>("ledger", "SELECT idempotency_key FROM journal_status_transitions WHERE journal_id = $1 AND to_status = 'POSTED'", journal));
    }

    [Fact]
    public async Task RejectedTransactionIsRejectedAndCannotPost()
    {
        var s = await Scenario.CreateAsync(stack);
        var journal = await s.DraftAsync("60000.00");

        var submitted = await Scenario.Expect(await s.CommandAsync(journal, "submit"), HttpStatusCode.OK);

        Assert.Equal("REJECTED", submitted["status"]!.GetValue<string>());
        Assert.Contains("AMOUNT_EXCEEDS_HARD_LIMIT", submitted["policyDecision"]!["reasonCodes"]!.AsArray().Select(c => c!.GetValue<string>()));
        await Scenario.ExpectProblem(await s.CommandAsync(journal, "post"), HttpStatusCode.Conflict, "JOURNAL_INVALID_STATE");
    }

    [Fact]
    public async Task ManualReviewKeepsTheJournalPendingAndCannotPost()
    {
        var s = await Scenario.CreateAsync(stack);
        var journal = await s.DraftAsync("20000.00");

        var submitted = await Scenario.Expect(await s.CommandAsync(journal, "submit"), HttpStatusCode.OK);

        Assert.Equal("PENDING_APPROVAL", submitted["status"]!.GetValue<string>());
        Assert.True(submitted["manualReviewRequired"]!.GetValue<bool>());
        await Scenario.ExpectProblem(await s.CommandAsync(journal, "post"), HttpStatusCode.Conflict, "JOURNAL_INVALID_STATE");
    }

    [Fact]
    public async Task UnreachablePolicyServiceLeavesTheJournalPendingAndIsRecoverable()
    {
        var s = await Scenario.CreateAsync(stack);
        var journal = await s.DraftAsync("100.00");
        await stack.SetProxyAsync(new { enabled = false });

        var submitted = await Scenario.Expect(await s.CommandAsync(journal, "submit"), HttpStatusCode.OK);
        Assert.Equal("PENDING_APPROVAL", submitted["status"]!.GetValue<string>());
        Assert.Equal("UNAVAILABLE", submitted["approvalFailure"]!.GetValue<string>());
        await Scenario.ExpectProblem(await s.CommandAsync(journal, "request-approval"), HttpStatusCode.ServiceUnavailable, "POLICY_UNAVAILABLE");
        await Scenario.ExpectProblem(await s.CommandAsync(journal, "post"), HttpStatusCode.Conflict, "JOURNAL_INVALID_STATE");
        Assert.Equal(0, await s.PolicyDecisionsAsync(journal));

        await stack.SetProxyAsync(new { enabled = true });
        var approved = await Scenario.Expect(await s.CommandAsync(journal, "request-approval"), HttpStatusCode.OK);
        Assert.Equal("APPROVED", approved["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task LostResponseAfterPolicyCommitsIsRecoveredWithoutDuplicates()
    {
        var s = await Scenario.CreateAsync(stack);
        var journal = await s.DraftAsync("100.00");
        // Requests reach the policy service; every response is cut before any byte returns.
        await stack.AddToxicAsync("drop-responses", "limit_data", "downstream", new { bytes = 0 });

        var submitted = await Scenario.Expect(await s.CommandAsync(journal, "submit"), HttpStatusCode.OK);

        Assert.Equal("PENDING_APPROVAL", submitted["status"]!.GetValue<string>());
        Assert.NotNull(submitted["approvalFailure"]);
        // The policy service committed exactly one decision despite the ledger's retries.
        Assert.Equal(1, await s.PolicyDecisionsAsync(journal));
        Assert.Equal(0, await s.LedgerEvidenceAsync(journal));

        await stack.RemoveToxicAsync("drop-responses");
        var recovered = await Scenario.Expect(await s.CommandAsync(journal, "request-approval"), HttpStatusCode.OK);

        Assert.Equal("APPROVED", recovered["status"]!.GetValue<string>());
        Assert.Equal(
            await s.ScalarAsync<Guid>("policy", "SELECT id FROM policy_decisions WHERE transaction_id = $1", journal),
            recovered["policyDecision"]!["decisionId"]!.GetValue<Guid>());
        Assert.Equal(1, await s.PolicyDecisionsAsync(journal));
        Assert.Equal(1, await s.LedgerEvidenceAsync(journal));
        Assert.Equal(1, await s.TransitionsAsync(journal, "APPROVED"));
    }

    [Fact]
    public async Task DuplicateAndConcurrentRequestsProduceOneDecision()
    {
        var s = await Scenario.CreateAsync(stack);
        var journal = await s.DraftAsync("100.00");

        var submits = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => s.CommandAsync(journal, "submit")));
        Assert.Single(submits, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(submits.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));

        var retries = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => s.CommandAsync(journal, "request-approval")));
        var decisionIds = new HashSet<Guid>();
        foreach (var retry in retries)
        {
            decisionIds.Add((await Scenario.Expect(retry, HttpStatusCode.OK))["policyDecision"]!["decisionId"]!.GetValue<Guid>());
        }

        Assert.Single(decisionIds);
        Assert.Equal(1, await s.PolicyDecisionsAsync(journal));
        Assert.Equal(1, await s.LedgerEvidenceAsync(journal));
        Assert.Equal(1, await s.TransitionsAsync(journal, "APPROVED"));
    }

    [Fact]
    public async Task ContractViolatingPolicyResponseFailsClosed()
    {
        var s = await Scenario.CreateAsync(stack);
        var journal = await s.DraftAsync("100.00");
        await stack.SetProxyAsync(new { upstream = Stack.BrokenPolicyUpstream });

        var submitted = await Scenario.Expect(await s.CommandAsync(journal, "submit"), HttpStatusCode.OK);

        Assert.Equal("PENDING_APPROVAL", submitted["status"]!.GetValue<string>());
        Assert.Equal("CONTRACT_VIOLATION", submitted["approvalFailure"]!.GetValue<string>());
        await Scenario.ExpectProblem(await s.CommandAsync(journal, "request-approval"), HttpStatusCode.BadGateway, "POLICY_CONTRACT_VIOLATION");
        Assert.Equal(0, await s.LedgerEvidenceAsync(journal));
    }

    [Fact]
    public async Task CorrelationIdSpansBothServices()
    {
        var s = await Scenario.CreateAsync(stack);
        var journal = await s.DraftAsync("100.00");
        var correlation = "it-corr-" + Guid.NewGuid().ToString("N")[..12];

        var response = await s.CommandAsync(journal, "submit", correlation);

        await Scenario.Expect(response, HttpStatusCode.OK);
        Assert.Equal(correlation, Assert.Single(response.Headers.GetValues("X-Correlation-Id")));
        Assert.Equal(correlation, await s.ScalarAsync<string>("policy", "SELECT correlation_id FROM policy_decisions WHERE transaction_id = $1", journal));
        Assert.Equal(correlation, await s.ScalarAsync<string>("ledger", "SELECT correlation_id FROM journal_policy_decisions WHERE journal_id = $1", journal));
        Assert.Contains(correlation, await stack.LogsAsync("policy"), StringComparison.Ordinal);
        Assert.Contains(correlation, await stack.LogsAsync("ledger"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialsNeverAppearInEitherServiceLog()
    {
        var s = await Scenario.CreateAsync(stack);
        await s.CommandAsync(await s.DraftAsync("100.00"), "submit");

        var logs = await stack.LogsAsync("ledger") + await stack.LogsAsync("policy");

        Assert.DoesNotContain(stack.DecisionToken, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(stack.AdminToken, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolicyDecisionApiRequiresTheServiceCredential()
    {
        using var anonymous = new HttpClient { BaseAddress = stack.Policy.BaseAddress };
        using var withAdminToken = new HttpClient { BaseAddress = stack.Policy.BaseAddress };
        withAdminToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stack.AdminToken);
        var body = new { contractVersion = "1.1.0" };

        await Scenario.ExpectProblem(await anonymous.PostAsJsonAsync("v1/policy-decisions", body), HttpStatusCode.Unauthorized, "SERVICE_AUTHENTICATION_REQUIRED");
        await Scenario.ExpectProblem(await withAdminToken.PostAsJsonAsync("v1/policy-decisions", body), HttpStatusCode.Unauthorized, "SERVICE_AUTHENTICATION_REQUIRED");
        await Scenario.ExpectProblem(await anonymous.GetAsync(new Uri("api/v1/policies", UriKind.Relative)), HttpStatusCode.Unauthorized, "ADMIN_AUTHENTICATION_REQUIRED");
    }

    [Theory]
    [InlineData("ledger_runtime", "policy")]
    [InlineData("policy_runtime", "ledger")]
    public async Task ServiceRolesCannotCrossTheDatabaseBoundary(string role, string foreignDatabase)
    {
        var own = role == "ledger_runtime" ? stack.LedgerRuntimeConnection : stack.PolicyRuntimeConnection;
        var foreign = new NpgsqlConnectionStringBuilder(own) { Database = foreignDatabase }.ConnectionString;

        await using (var ok = new NpgsqlConnection(own))
        {
            await ok.OpenAsync();
        }

        await using var denied = new NpgsqlConnection(foreign);
        var error = await Assert.ThrowsAsync<PostgresException>(() => denied.OpenAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task LedgerStaysReadyWhenPolicyIsDownButReportsTheDependency()
    {
        await stack.SetProxyAsync(new { enabled = false });

        var ready = await stack.Ledger.GetAsync(new Uri("health/ready", UriKind.Relative));
        var dependencies = await stack.Ledger.GetStringAsync(new Uri("health/dependencies", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Degraded", dependencies);
        await stack.SetProxyAsync(new { enabled = true });
        Assert.Equal("Healthy", await stack.Ledger.GetStringAsync(new Uri("health/dependencies", UriKind.Relative)));
    }
}
