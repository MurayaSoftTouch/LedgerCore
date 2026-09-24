using Microsoft.EntityFrameworkCore;

namespace LedgerCore.Ledger.Api.Persistence;

internal static class LedgerDatabase
{
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();
}
