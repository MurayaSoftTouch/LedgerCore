using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LedgerCore.Ledger.Api.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c>. Migrations run as the schema owner (<c>ledger_app</c>), never as the
/// runtime role; pass the owner connection with <c>--connection</c> or <c>LEDGER_MIGRATIONS_CONNECTION</c>.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("LEDGER_MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=ledger;Username=ledger_app";
        var options = new DbContextOptionsBuilder<LedgerDbContext>();
        LedgerDatabase.Configure(options, connection);
        return new LedgerDbContext(options.Options);
    }
}
