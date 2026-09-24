using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Npgsql;

namespace LedgerCore.IntegrationTests;

/// <summary>
/// The whole backend on one Docker network, built from the repository (nothing from the developer
/// machine): PostgreSQL 18.6 initialised by infra/docker/postgres/init, the ledger migration bundle,
/// the policy service, Toxiproxy, and the ledger API, which reaches the policy service
/// <em>only</em> through Toxiproxy so tests can cut the connection or drop responses. A fixed
/// HTTP echo server stands in for a policy service that breaks the contract.
/// </summary>
public sealed class Stack : IAsyncLifetime
{
    public const string ProxyName = "policy";
    public const string PolicyUpstream = "policy-service:8081";
    public const string BrokenPolicyUpstream = "broken-policy:5678";

    // Throwaway credentials generated per run; never written anywhere but the containers.
    public readonly string DecisionToken = "it-decision-" + Guid.NewGuid().ToString("N");
    public readonly string AdminToken = "it-admin-" + Guid.NewGuid().ToString("N");
    private readonly string _adminPassword = Guid.NewGuid().ToString("N");
    private readonly string _ledgerOwner = Guid.NewGuid().ToString("N");
    private readonly string _ledgerRuntime = Guid.NewGuid().ToString("N");
    private readonly string _policyOwner = Guid.NewGuid().ToString("N");
    private readonly string _policyRuntime = Guid.NewGuid().ToString("N");

    private readonly List<IAsyncDisposable> _resources = [];
    private INetwork _network = null!;
    private IContainer _postgres = null!;
    private IContainer _policy = null!;
    private IContainer _ledger = null!;
    private IContainer _toxiproxy = null!;

    /// <summary>Every credential this run generated: service tokens and all database passwords.</summary>
    public IReadOnlyList<string> Secrets => [DecisionToken, AdminToken, _adminPassword, _ledgerOwner, _ledgerRuntime, _policyOwner, _policyRuntime];

    public HttpClient Ledger { get; private set; } = null!;

    public HttpClient Policy { get; private set; } = null!;

    public HttpClient Toxiproxy { get; private set; } = null!;

    public string LedgerRuntimeConnection => Connection("ledger", "ledger_runtime", _ledgerRuntime);

    public string PolicyRuntimeConnection => Connection("policy", "policy_runtime", _policyRuntime);

    public async Task InitializeAsync()
    {
        _network = Track(new NetworkBuilder().WithName("ledgercore-it-" + Guid.NewGuid().ToString("N")[..8]).Build());
        await _network.CreateAsync();

        var ledgerRuntimeImage = Image("services/ledger-api", "runtime", "ledgercore-it/ledger-api:local");
        var ledgerMigrateImage = Image("services/ledger-api", "migrate", "ledgercore-it/ledger-migrate:local");
        var policyImage = Image("services/policy-service", null, "ledgercore-it/policy-service:local");
        await Task.WhenAll(ledgerRuntimeImage.CreateAsync(), ledgerMigrateImage.CreateAsync(), policyImage.CreateAsync());

        _postgres = Track(new ContainerBuilder("postgres:18.6-alpine")
            .WithNetwork(_network).WithNetworkAliases("postgres")
            .WithEnvironment("POSTGRES_USER", "ledgercore_admin")
            .WithEnvironment("POSTGRES_PASSWORD", _adminPassword)
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithEnvironment("LEDGER_DB_PASSWORD", _ledgerOwner)
            .WithEnvironment("LEDGER_RUNTIME_DB_PASSWORD", _ledgerRuntime)
            .WithEnvironment("POLICY_DB_PASSWORD", _policyOwner)
            .WithEnvironment("POLICY_RUNTIME_DB_PASSWORD", _policyRuntime)
            .WithResourceMapping(new FileInfo(Repository.Path("infra/docker/postgres/init/01-create-databases.sh")), "/docker-entrypoint-initdb.d/")
            .WithPortBinding(5432, true)
            // The init script restarts the server; wait for the final one on TCP.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                "sh", "-c", "pg_isready -h 127.0.0.1 -U ledgercore_admin -d ledger && pg_isready -h 127.0.0.1 -U ledgercore_admin -d policy"))
            .Build());
        await _postgres.StartAsync();

        var migrate = Track(new ContainerBuilder(ledgerMigrateImage)
            .WithNetwork(_network)
            .WithEnvironment("LEDGER_MIGRATIONS_CONNECTION", $"Host=postgres;Port=5432;Database=ledger;Username=ledger_app;Password={_ledgerOwner}")
            .WithWaitStrategy(Wait.ForUnixContainer())
            .Build());
        await migrate.StartAsync();
        var exitCode = await migrate.GetExitCodeAsync();
        if (exitCode != 0)
        {
            var (stdout, stderr) = await migrate.GetLogsAsync();
            throw new InvalidOperationException($"ledger migrations failed ({exitCode}): {stdout}{stderr}");
        }

        _policy = Track(new ContainerBuilder(policyImage)
            .WithNetwork(_network).WithNetworkAliases("policy-service")
            .WithEnvironment("POLICY_DB_URL", "jdbc:postgresql://postgres:5432/policy")
            .WithEnvironment("POLICY_RUNTIME_DB_PASSWORD", _policyRuntime)
            .WithEnvironment("POLICY_DB_PASSWORD", _policyOwner)
            .WithEnvironment("POLICY_DECISION_API_TOKEN", DecisionToken)
            .WithEnvironment("POLICY_ADMIN_API_TOKEN", AdminToken)
            .WithEnvironment("LOG_FORMAT", "ecs")
            .WithPortBinding(8081, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8081).ForPath("/actuator/health/readiness")))
            .Build());

        _toxiproxy = Track(new ContainerBuilder("ghcr.io/shopify/toxiproxy:2.12.0")
            .WithNetwork(_network).WithNetworkAliases("toxiproxy")
            .WithPortBinding(8474, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8474).ForPath("/version")))
            .Build());

        // Answers every request with 200 and a body that is valid JSON but violates the contract.
        var broken = Track(new ContainerBuilder("hashicorp/http-echo:1.0.0")
            .WithNetwork(_network).WithNetworkAliases("broken-policy")
            .WithCommand("-listen=:5678", """-text={"contractVersion":"1.1.0","decisionId":"00000000-0000-4000-8000-000000000001","transactionId":"00000000-0000-4000-8000-000000000002","policyVersion":"broken@1","decision":"MAYBE","reasonCodes":[],"evaluatedAt":"2026-09-23T09:15:00Z"}""")
            .Build());
        await Task.WhenAll(_policy.StartAsync(), _toxiproxy.StartAsync(), broken.StartAsync());

        Policy = Track(new HttpClient { BaseAddress = new Uri($"http://{_policy.Hostname}:{_policy.GetMappedPublicPort(8081)}/") });
        Policy.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        Policy.DefaultRequestHeaders.Add("X-Actor-Id", "integration-admin");
        Toxiproxy = Track(new HttpClient { BaseAddress = new Uri($"http://{_toxiproxy.Hostname}:{_toxiproxy.GetMappedPublicPort(8474)}/") });
        (await Toxiproxy.PostAsJsonAsync("proxies", new { name = ProxyName, listen = "0.0.0.0:8666", upstream = PolicyUpstream, enabled = true })).EnsureSuccessStatusCode();

        _ledger = Track(new ContainerBuilder(ledgerRuntimeImage)
            .WithNetwork(_network).WithNetworkAliases("ledger-api")
            .WithEnvironment("ConnectionStrings__Ledger", $"Host=postgres;Port=5432;Database=ledger;Username=ledger_runtime;Password={_ledgerRuntime}")
            .WithEnvironment("Ledger__PolicyServiceBaseUrl", "http://toxiproxy:8666")
            .WithEnvironment("Ledger__PolicyServiceToken", DecisionToken)
            // Wider than the production defaults (2000/800 ms) so a cold JVM's first evaluation
            // is not mistaken for an outage; retry behaviour itself is unit-tested in the ledger.
            .WithEnvironment("Ledger__PolicyDecisionTimeoutMs", "5000")
            .WithEnvironment("Ledger__PolicyAttemptTimeoutMs", "2000")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/health/ready")))
            .Build());
        await _ledger.StartAsync();
        Ledger = Track(new HttpClient { BaseAddress = new Uri($"http://{_ledger.Hostname}:{_ledger.GetMappedPublicPort(8080)}/"), Timeout = TimeSpan.FromSeconds(30) });
        Ledger.DefaultRequestHeaders.Add("X-Actor-Id", "integration-tester");
    }

    public async Task DisposeAsync()
    {
        for (var i = _resources.Count - 1; i >= 0; i--)
        {
            await _resources[i].DisposeAsync();
        }
    }

    /// <summary>Connection as the cluster superuser, for assertions only (never used by a service).</summary>
    public NpgsqlConnection Admin(string database) => new(Connection(database, "ledgercore_admin", _adminPassword));

    public async Task<string> LogsAsync(string service)
    {
        var container = service switch { "ledger" => _ledger, "policy" => _policy, _ => throw new ArgumentOutOfRangeException(nameof(service)) };
        var (stdout, stderr) = await container.GetLogsAsync();
        return stdout + stderr;
    }

    /// <summary>Stops the shared PostgreSQL container (both services lose their database).</summary>
    public Task StopPostgresAsync() => _postgres.StopAsync();

    /// <summary>Starts it again with its data; the host port may change, the network alias does not.</summary>
    public Task StartPostgresAsync() => _postgres.StartAsync();

    public Task SetProxyAsync(object settings) => Post($"proxies/{ProxyName}", settings);

    public Task AddToxicAsync(string name, string type, string stream, object attributes) =>
        Post($"proxies/{ProxyName}/toxics", new { name, type, stream, toxicity = 1.0, attributes });

    public async Task RemoveToxicAsync(string name) =>
        (await Toxiproxy.DeleteAsync(new Uri($"proxies/{ProxyName}/toxics/{name}", UriKind.Relative))).EnsureSuccessStatusCode();

    /// <summary>Resets the proxy to a healthy, direct path to the real policy service.</summary>
    public async Task ResetProxyAsync()
    {
        var toxics = JsonNode.Parse(await Toxiproxy.GetStringAsync(new Uri($"proxies/{ProxyName}/toxics", UriKind.Relative)))!.AsArray();
        foreach (var toxic in toxics)
        {
            await RemoveToxicAsync(toxic!["name"]!.GetValue<string>());
        }

        await SetProxyAsync(new { enabled = true, upstream = PolicyUpstream });
    }

    private async Task Post(string path, object body) =>
        (await Toxiproxy.PostAsJsonAsync(path, body)).EnsureSuccessStatusCode();

    private string Connection(string database, string user, string password) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = _postgres.Hostname,
            Port = _postgres.GetMappedPublicPort(5432),
            Database = database,
            Username = user,
            Password = password,
            Pooling = false,
        }.ConnectionString;

    private static IFutureDockerImage Image(string directory, string? target, string name)
    {
        var builder = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Repository.Path(directory))
            .WithDockerfile("Dockerfile")
            .WithName(name)
            .WithDeleteIfExists(false)
            .WithCleanUp(false);
        return (target is null ? builder : builder.WithTarget(target)).Build();
    }

    private T Track<T>(T resource)
        where T : class
    {
        switch (resource)
        {
            case IAsyncDisposable asyncDisposable:
                _resources.Add(asyncDisposable);
                break;
            case IDisposable disposable:
                _resources.Add(new DisposableAdapter(disposable));
                break;
        }

        return resource;
    }

    private sealed class DisposableAdapter(IDisposable inner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

[CollectionDefinition(Name)]
public sealed class StackGroup : ICollectionFixture<Stack>
{
    public const string Name = "stack";
}

internal static class Repository
{
    public static string Path(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "docker-compose.yml")))
            {
                return System.IO.Path.Combine(dir.FullName, relative);
            }
        }

        throw new InvalidOperationException("repository root not found");
    }
}
