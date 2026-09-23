using LedgerCore.Ledger.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace LedgerCore.Ledger.Api.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL 18.6 container per test run, initialised with the repository's real init script
/// (roles, databases, default privileges). Migrations are applied as the schema owner
/// (<c>ledger_app</c>); the application and tests run as <c>ledger_runtime</c> unless a test
/// deliberately uses the owner to prove a trigger holds without relying on privileges.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string OwnerPassword = "test-ledger-owner";
    private const string RuntimePassword = "test-ledger-runtime";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.6-alpine")
        .WithUsername("ledgercore_admin")
        .WithPassword("test-admin")
        .WithDatabase("postgres")
        .WithEnvironment("LEDGER_DB_PASSWORD", OwnerPassword)
        .WithEnvironment("LEDGER_RUNTIME_DB_PASSWORD", RuntimePassword)
        .WithEnvironment("POLICY_DB_PASSWORD", "test-policy")
        .WithResourceMapping(new FileInfo(RepositoryPaths.PostgresInitScript), "/docker-entrypoint-initdb.d/")
        .Build();

    public string RuntimeConnectionString { get; private set; } = string.Empty;

    public string OwnerConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        RuntimeConnectionString = ConnectionFor("ledger_runtime", RuntimePassword);
        OwnerConnectionString = ConnectionFor("ledger_app", OwnerPassword);

        await using var owner = CreateContext(OwnerConnectionString);
        await owner.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    internal LedgerDbContext CreateRuntimeContext(params IInterceptor[] interceptors) =>
        CreateContext(RuntimeConnectionString, interceptors);

    public NpgsqlConnection CreateRuntimeConnection() => new(RuntimeConnectionString);

    public NpgsqlConnection CreateOwnerConnection() => new(OwnerConnectionString);

    private static LedgerDbContext CreateContext(string connectionString, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>();
        LedgerDatabase.Configure(options, connectionString);
        options.AddInterceptors(interceptors);
        return new LedgerDbContext(options.Options);
    }

    private string ConnectionFor(string role, string password) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = "ledger",
            Username = role,
            Password = password,
            // Enough connections for the concurrency tests without exhausting the server.
            MaxPoolSize = 30,
        }.ConnectionString;
}

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
