using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Journals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LedgerCore.Ledger.Api.Persistence.Configurations;

internal sealed class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> builder)
    {
        builder.ToTable("journal_entries", t =>
        {
            t.HasCheckConstraint("ck_journal_entries_amount_positive", "amount > 0");
            t.HasCheckConstraint("ck_journal_entries_direction", $"direction IN ({EnumText.SqlList<EntryDirection>()})");
            t.HasCheckConstraint("ck_journal_entries_line_number", "line_number >= 1");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Currency).HasConversion(CurrencyConversion.Converter).HasColumnType("character(3)");
        builder.Property(e => e.Direction).HasConversion(EnumText.Converter<EntryDirection>()).HasMaxLength(8);
        builder.Property(e => e.Amount).HasColumnType("numeric(22,4)");
        builder.Property(e => e.Memo).HasMaxLength(500);

        // The line's account must belong to the journal's ledger and currency.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(e => new { e.AccountId, e.LedgerId, e.Currency })
            .HasPrincipalKey(a => new { a.Id, a.LedgerId, a.Currency })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_journal_entries_account");

        builder.HasIndex(e => new { e.JournalId, e.LineNumber }).IsUnique();
    }
}
