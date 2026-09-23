using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Monetary;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainLedger = LedgerCore.Ledger.Domain.Ledgers.Ledger;

namespace LedgerCore.Ledger.Api.Persistence.Configurations;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", t =>
        {
            t.HasCheckConstraint("ck_accounts_type", $"type IN ({EnumText.SqlList<AccountType>()})");
            t.HasCheckConstraint("ck_accounts_active_state", "(is_active AND deactivated_at IS NULL) OR (NOT is_active AND deactivated_at IS NOT NULL)");
        });

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Code).HasMaxLength(32);
        builder.Property(a => a.Name).HasMaxLength(200);
        builder.Property(a => a.Type).HasConversion(EnumText.Converter<AccountType>()).HasMaxLength(16);
        builder.Property(a => a.Currency).HasConversion(CurrencyConversion.Converter).HasColumnType("character(3)");

        // Target of the entries' composite foreign key: same ledger, same currency.
        builder.HasAlternateKey(a => new { a.Id, a.LedgerId, a.Currency });

        builder.HasIndex(a => new { a.LedgerId, a.Code }).IsUnique().HasDatabaseName(DatabaseConstraints.AccountCodeUnique);

        builder.HasOne<DomainLedger>().WithMany().HasForeignKey(a => a.LedgerId).OnDelete(DeleteBehavior.Restrict);
    }
}
