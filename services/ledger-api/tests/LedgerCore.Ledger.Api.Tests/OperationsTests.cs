using System.Net;
using System.Text;
using LedgerCore.Ledger.Api.Operations;
using LedgerCore.Ledger.Api.Persistence;
using LedgerCore.Ledger.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>Health, degradation, limits and diagnostics of the ledger host (Milestone 5, ADR-016).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class OperationsTests(PostgresFixture db) : IDisposable
{
    private readonly LedgerApiFactory _factory = new(db);

    public void Dispose() => _factory.Dispose();

    private WebApplicationFactory<Program> WithConnection(string connectionString) =>
        _factory.WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Ledger", connectionString));

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    /// <summary>A fresh database in the test cluster, reachable by ledger_runtime, with no ledger schema.</summary>
    private async Task<string> EmptyDatabaseAsync()
    {
        var name = "empty_" + Guid.NewGuid().ToString("N")[..12];
        await using var superuser = db.CreateSuperuserConnection();
        await Sql.ExecuteAsync(superuser, $"CREATE DATABASE {name}");
        await Sql.ExecuteAsync(superuser, $"GRANT CONNECT ON DATABASE {name} TO ledger_runtime");
        return new NpgsqlConnectionStringBuilder(db.RuntimeConnectionString) { Database = name }.ConnectionString;
    }

    [Fact]
    public async Task TheSchemaCheckIsHealthyOnTheMigratedDatabase()
    {
        await using var ctx = db.CreateRuntimeContext();

        var result = await new LedgerSchemaHealthCheck(ctx).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task AnUnmigratedDatabaseMakesTheLedgerUnreadyButLive()
    {
        using var factory = WithConnection(await EmptyDatabaseAsync());
        using var client = factory.CreateClient();

        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await BodyAsync(ready));
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task APartlyMigratedOrNewerSchemaIsReported()
    {
        var connection = await EmptyDatabaseAsync();
        var owner = new NpgsqlConnectionStringBuilder(connection) { Username = "ledgercore_admin", Password = "test-admin" }.ConnectionString;
        await using (var ctx = db.CreateRuntimeContext())
        {
            var all = ctx.Database.GetMigrations().ToList();
            await using var admin = new NpgsqlConnection(owner);
            await Sql.ExecuteAsync(admin, "CREATE TABLE __ef_migrations_history (migration_id varchar(150) PRIMARY KEY, product_version varchar(32) NOT NULL)");
            await Sql.ExecuteAsync(admin, "GRANT SELECT ON __ef_migrations_history TO ledger_runtime");
            foreach (var id in all.Take(all.Count - 1))
            {
                await Sql.ExecuteAsync(admin, "INSERT INTO __ef_migrations_history VALUES ($1, '10.0')", id);
            }

            await using var partly = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connection, n => n.MigrationsHistoryTable("__ef_migrations_history")).UseSnakeCaseNamingConvention().Options);
            var missingOne = await new LedgerSchemaHealthCheck(partly).CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Unhealthy, missingOne.Status);
            Assert.Equal("1 ledger migration(s) not applied", missingOne.Description);

            await Sql.ExecuteAsync(admin, "INSERT INTO __ef_migrations_history VALUES ($1, '10.0'), ('29990101000000_FromTheFuture', '10.0')", all[^1]);
            var newer = await new LedgerSchemaHealthCheck(partly).CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Degraded, newer.Status);
        }
    }

    [Fact]
    public async Task AnUnreachableDatabaseIs503EverywhereButLiveness()
    {
        using var factory = WithConnection("Host=127.0.0.1;Port=1;Database=ledger;Username=ledger_runtime;Password=unused;Timeout=2");
        var api = ApiClient.Create(_factory, factory);

        var create = await api.Http.PostAsync(new Uri("/api/v1/ledgers", UriKind.Relative), new StringContent("""{"code":"X-1","name":"x"}""", Encoding.UTF8, "application/json"));
        var read = await api.Http.GetAsync(new Uri($"/api/v1/ledgers/{Guid.NewGuid()}", UriKind.Relative));

        await ApiClient.ExpectProblemAsync(create, HttpStatusCode.ServiceUnavailable, "LEDGER_DATABASE_UNAVAILABLE");
        await ApiClient.ExpectProblemAsync(read, HttpStatusCode.ServiceUnavailable, "LEDGER_DATABASE_UNAVAILABLE");
        Assert.Equal("1", Assert.Single(create.Headers.GetValues("Retry-After")));
        Assert.DoesNotContain("127.0.0.1", await BodyAsync(read), StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", await BodyAsync(read), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await api.Http.GetAsync(new Uri("/health/ready", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Http.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task AnExhaustedConnectionPoolIs503AndRecovers()
    {
        var tiny = new NpgsqlConnectionStringBuilder(db.RuntimeConnectionString) { MaxPoolSize = 1, Timeout = 1, ApplicationName = "pool-test-" + Guid.NewGuid().ToString("N")[..8] }.ConnectionString;
        using var factory = WithConnection(tiny);
        var api = ApiClient.Create(_factory, factory);
        var ledger = await api.CreateLedgerAsync();

        // Hold the only pooled connection (same process, same connection string, same pool).
        await using (var holder = new NpgsqlConnection(tiny))
        {
            await holder.OpenAsync();
            await ApiClient.ExpectProblemAsync(
                await api.Http.GetAsync(new Uri($"/api/v1/ledgers/{ledger}", UriKind.Relative)), HttpStatusCode.ServiceUnavailable, "LEDGER_DATABASE_UNAVAILABLE");
        }

        await ApiClient.ExpectAsync(await api.Http.GetAsync(new Uri($"/api/v1/ledgers/{ledger}", UriKind.Relative)), HttpStatusCode.OK);
    }

    [Fact]
    public async Task ADeclaredBodyOverTheLimitIs413WithoutBeingRead()
    {
        var api = ApiClient.Create(_factory);
        var body = new string('x', (int)RequestLimits.MaximumBodyBytes + 1);

        var response = await api.Http.PostAsync(new Uri("/api/v1/ledgers", UriKind.Relative), new StringContent(body, Encoding.UTF8, "application/json"));

        await ApiClient.ExpectProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "REQUEST_TOO_LARGE");
        Assert.DoesNotContain("xxxx", await BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeeplyNestedJsonIsRejectedWithoutEchoingIt()
    {
        var api = ApiClient.Create(_factory);
        var nested = new StringBuilder("""{"code":"D-1","name":"x","extra":""");
        for (var i = 0; i < 40; i++)
        {
            nested.Append("""{"a":""");
        }

        nested.Append('1').Append('}', 41);

        var response = await api.Http.PostAsync(new Uri("/api/v1/ledgers", UriKind.Relative), new StringContent(nested.ToString(), Encoding.UTF8, "application/json"));

        await ApiClient.ExpectProblemAsync(response, HttpStatusCode.BadRequest, "REQUEST_INVALID");
        Assert.DoesNotContain("\"a\"", await BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnexpectedErrorIsAGeneric500WithoutInternals()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(new ExplodingClock())));
        var api = ApiClient.Create(_factory, factory);

        var response = await api.Http.PostAsync(new Uri("/api/v1/ledgers", UriKind.Relative), new StringContent("""{"code":"E-1","name":"x"}""", Encoding.UTF8, "application/json"));

        await ApiClient.ExpectProblemAsync(response, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        var body = await BodyAsync(response);
        Assert.DoesNotContain("secret clock failure", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ExplodingClock", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachRequestLogsOneLineWithTheRouteTemplateAndNoIdentifiersOrBodies()
    {
        using var logs = new CollectingLoggerProvider();
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(logs)));
        var api = ApiClient.Create(_factory, factory);
        var ledger = await api.CreateLedgerAsync();

        await api.Http.GetAsync(new Uri($"/api/v1/ledgers/{ledger}?secret=query-value", UriKind.Relative));

        var line = Assert.Single(logs.Entries, e => e.Message.StartsWith("HTTP GET /{ledgerId:guid}", StringComparison.Ordinal) || e.Message.StartsWith("HTTP GET /api/v1/ledgers/{ledgerId:guid}", StringComparison.Ordinal));
        Assert.Matches(@"responded 200 in \d+ ms$", line.Message);
        Assert.DoesNotContain(ledger.ToString(), line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("query-value", StringComparison.Ordinal));
    }

    [Fact]
    public void OutboxStatesDescribeWaitingNotFailure()
    {
        var threshold = TimeSpan.FromMinutes(5);

        Assert.Equal(OutboxState.Empty, OutboxDiagnostics.Classify(null, threshold));
        Assert.Equal(OutboxState.Pending, OutboxDiagnostics.Classify(TimeSpan.FromMinutes(5), threshold));
        Assert.Equal(OutboxState.Aging, OutboxDiagnostics.Classify(TimeSpan.FromMinutes(5.01), threshold));
    }

    [Fact]
    public async Task TheOutboxEndpointReportsCountsAndAgesButNoPayloads()
    {
        var type = "DiagnosticTest" + Guid.NewGuid().ToString("N")[..8];
        await using (var superuser = db.CreateSuperuserConnection())
        {
            await superuser.OpenAsync();
            await using var tx = await superuser.BeginTransactionAsync();
            await Sql.ExecuteAsync(superuser, "SET LOCAL session_replication_role = replica");
            await Sql.ExecuteAsync(
                superuser,
                "INSERT INTO outbox_events (id, aggregate_type, aggregate_id, event_type, payload, created_at, published_at) VALUES " +
                "(gen_random_uuid(), 'Test', gen_random_uuid(), $1, '{\"secret\":\"payload-value\"}', now() - interval '2 hours', NULL), " +
                "(gen_random_uuid(), 'Test', gen_random_uuid(), $1, '{}', now() - interval '1 minute', NULL), " +
                "(gen_random_uuid(), 'Test', gen_random_uuid(), $1, '{}', now() - interval '3 hours', now())",
                type);
            await tx.CommitAsync();
        }

        var api = ApiClient.Create(_factory);
        var response = await api.Http.GetAsync(new Uri("/ops/outbox", UriKind.Relative));
        var status = await ApiClient.ExpectAsync(response, HttpStatusCode.OK);

        var mine = status["eventTypes"]!.AsArray().Single(t => t!["eventType"]!.GetValue<string>() == type)!;
        Assert.Equal(2, mine["pending"]!.GetValue<int>());
        Assert.Equal("AGING", mine["state"]!.GetValue<string>());
        Assert.InRange(mine["oldestPendingAgeSeconds"]!.GetValue<long>(), 7_190, 7_300);
        Assert.Equal("AGING", status["state"]!.GetValue<string>());
        Assert.Equal(300, status["agingThresholdSeconds"]!.GetValue<long>());
        Assert.False(status["publisherConfigured"]!.GetValue<bool>());
        Assert.DoesNotContain("payload-value", await BodyAsync(response), StringComparison.Ordinal);
    }

    private sealed class ExplodingClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("secret clock failure");
    }
}
