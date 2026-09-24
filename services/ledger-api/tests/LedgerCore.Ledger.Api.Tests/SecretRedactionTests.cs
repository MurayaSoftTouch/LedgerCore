using System.Net;
using System.Text;
using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>
/// No secret reaches the logs (Milestone 5): the policy service credential, database passwords,
/// inbound Authorization headers, raw idempotency keys and request bodies. Each test captures every
/// log message, exception and scope value of real requests, successful and failing, and searches
/// them all.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SecretRedactionTests(PostgresFixture db) : IDisposable
{
    private const string InboundCredential = "inbound-secret-credential-7c1f9a";
    private const string BodySecret = "body-secret-value-51b2";
    private const string KeySecret = "key-secret-value-93e0";
    private const string DbPassword = "db-secret-password-40ad";

    private readonly LedgerApiFactory _factory = new(db);
    private readonly CollectingLoggerProvider _logs = new();

    public void Dispose()
    {
        _factory.Dispose();
        _logs.Dispose();
    }

    private WebApplicationFactory<Program> Capturing(WebApplicationFactory<Program> host) =>
        host.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(_logs)));

    private string AllLogs() => string.Join('\n', _logs.Entries.Select(e => e.Message));

    private static readonly string[] AlwaysSecret = [LedgerApiFactory.TestPolicyToken, "test-ledger-runtime", "test-ledger-owner", "test-admin"];

    private void AssertNoSecrets(params string[] extra)
    {
        var logs = AllLogs();
        Assert.NotEmpty(_logs.Entries);
        foreach (var secret in AlwaysSecret.Concat(extra))
        {
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Password=", logs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulAndFailedRequestsLogNoSecrets()
    {
        using var host = Capturing(_factory);
        var api = ApiClient.Create(_factory, host);
        api.Http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + InboundCredential);
        var ledger = await api.CreateLedgerAsync();
        var rent = await api.OpenAccountAsync(ledger, "5000", "EXPENSE");
        var cash = await api.OpenAccountAsync(ledger, "1000", "ASSET");
        var journal = await api.CreateJournalAsync(ledger);
        await api.AddEntryAsync(ledger, journal, rent, "DEBIT", "10.00");
        await api.AddEntryAsync(ledger, journal, cash, "CREDIT", "10.00");
        await api.CommandAsync(ledger, journal, "submit");
        await api.ApproveAsync(ledger, journal);
        await api.CommandAsync(ledger, journal, "post", KeySecret);
        await api.CommandAsync(ledger, journal, "post", KeySecret);               // replay
        await api.CommandAsync(ledger, journal, "reverse", KeySecret + "-x!");    // invalid key

        // Failures: malformed JSON, a rule violation, an oversized body, a missing actor.
        await api.Http.PostAsync(new Uri("/api/v1/ledgers", UriKind.Relative), new StringContent("{\"code\":\"" + BodySecret + "\",", Encoding.UTF8, "application/json"));
        await api.Http.PostAsync(new Uri($"/api/v1/ledgers/{ledger}/journals/{journal}/entries", UriKind.Relative), new StringContent("{\"accountId\":\"" + cash + "\",\"direction\":\"DEBIT\",\"amount\":\"1.00\",\"memo\":\"" + BodySecret + "\"}", Encoding.UTF8, "application/json"));
        await api.Http.PostAsync(new Uri("/api/v1/ledgers", UriKind.Relative), new StringContent(new string('x', 70_000) + BodySecret, Encoding.UTF8, "application/json"));
        using var anonymous = host.CreateClient();
        await anonymous.PostAsync(new Uri("/api/v1/ledgers", UriKind.Relative), new StringContent("{\"code\":\"" + BodySecret + "\",\"name\":\"n\"}", Encoding.UTF8, "application/json"));

        Assert.Contains(_logs.Entries, e => e.Message.Contains("POST_JOURNAL", StringComparison.Ordinal) && e.Message.Contains("REPLAYED", StringComparison.Ordinal));
        AssertNoSecrets(InboundCredential, BodySecret, KeySecret);
    }

    [Fact]
    public async Task TheRealPolicyClientNeverLogsItsCredential()
    {
        using var real = new LedgerApiFactory(db, useRealPolicyClient: true);
        using var host = Capturing(real);
        var api = ApiClient.Create(real, host);
        var ledger = await api.CreateLedgerAsync();
        var rent = await api.OpenAccountAsync(ledger, "5000", "EXPENSE");
        var cash = await api.OpenAccountAsync(ledger, "1000", "ASSET");
        var journal = await api.CreateJournalAsync(ledger);
        await api.AddEntryAsync(ledger, journal, rent, "DEBIT", "10.00");
        await api.AddEntryAsync(ledger, journal, cash, "CREDIT", "10.00");

        // The policy service is unreachable (the factory's TEST-NET address): the client sends its
        // bearer credential, fails, retries, and logs the failure.
        var submitted = await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "submit"), HttpStatusCode.OK);

        Assert.NotNull(submitted["approvalFailure"]);
        Assert.Contains(_logs.Entries, e => e.Message.Contains("Policy evaluation failed", StringComparison.Ordinal));
        AssertNoSecrets();
    }

    [Fact]
    public async Task ADatabaseFailureNeverLogsTheConnectionString()
    {
        var connection = new NpgsqlConnectionStringBuilder(db.RuntimeConnectionString) { Password = DbPassword }.ConnectionString;
        using var host = Capturing(_factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Ledger", connection)));
        var api = ApiClient.Create(_factory, host);

        var response = await api.Http.PostAsync(new Uri("/api/v1/ledgers", UriKind.Relative), new StringContent("{\"code\":\"P-1\",\"name\":\"n\"}", Encoding.UTF8, "application/json"));
        await api.Http.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(DbPassword, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertNoSecrets(DbPassword);
    }
}
