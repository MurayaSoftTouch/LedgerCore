using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Journals;
using Microsoft.EntityFrameworkCore;
using DomainLedger = LedgerCore.Ledger.Domain.Ledgers.Ledger;

namespace LedgerCore.Ledger.Api.Persistence;

internal sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<DomainLedger> Ledgers => Set<DomainLedger>();

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<Journal> Journals => Set<Journal>();

    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();

    /// <summary>Written only by a database trigger; read-only to the application.</summary>
    public DbSet<JournalStatusTransition> JournalStatusTransitions => Set<JournalStatusTransition>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LedgerDbContext).Assembly);
}
