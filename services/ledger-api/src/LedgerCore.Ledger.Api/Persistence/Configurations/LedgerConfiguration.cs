using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainLedger = LedgerCore.Ledger.Domain.Ledgers.Ledger;

namespace LedgerCore.Ledger.Api.Persistence.Configurations;

internal sealed class LedgerConfiguration : IEntityTypeConfiguration<DomainLedger>
{
    public void Configure(EntityTypeBuilder<DomainLedger> builder)
    {
        builder.ToTable("ledgers");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Code).HasMaxLength(32);
        builder.Property(l => l.Name).HasMaxLength(200);
        builder.HasIndex(l => l.Code).IsUnique().HasDatabaseName(DatabaseConstraints.LedgerCodeUnique);
    }
}
