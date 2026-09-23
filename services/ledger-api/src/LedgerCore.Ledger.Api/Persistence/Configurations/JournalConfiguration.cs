using LedgerCore.Ledger.Domain.Journals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainLedger = LedgerCore.Ledger.Domain.Ledgers.Ledger;

namespace LedgerCore.Ledger.Api.Persistence.Configurations;

internal sealed class JournalConfiguration : IEntityTypeConfiguration<Journal>
{
    public void Configure(EntityTypeBuilder<Journal> builder)
    {
        builder.ToTable("journals", t =>
        {
            t.HasCheckConstraint("ck_journals_status", $"status IN ({EnumText.SqlList<JournalStatus>()})");
            t.HasCheckConstraint("ck_journals_transaction_type", $"transaction_type IN ({EnumText.SqlList<JournalType>()})");
            t.HasCheckConstraint("ck_journals_not_self_reversal", "reverses_journal_id IS NULL OR reverses_journal_id <> id");

            // Lifecycle columns are present exactly when the state implies them (ADR-006).
            t.HasCheckConstraint(
                "ck_journals_submitted_fields",
                "(status = 'DRAFT' AND submitted_at IS NULL AND submitted_by IS NULL) OR " +
                "(status <> 'DRAFT' AND submitted_at IS NOT NULL AND submitted_by IS NOT NULL)");
            t.HasCheckConstraint(
                "ck_journals_approved_fields",
                "(status IN ('APPROVED', 'POSTED') AND approved_at IS NOT NULL AND approved_by IS NOT NULL) OR " +
                "(status NOT IN ('APPROVED', 'POSTED') AND approved_at IS NULL AND approved_by IS NULL)");
            t.HasCheckConstraint(
                "ck_journals_rejected_fields",
                "(status = 'REJECTED' AND rejected_at IS NOT NULL AND rejected_by IS NOT NULL AND rejection_reason IS NOT NULL) OR " +
                "(status <> 'REJECTED' AND rejected_at IS NULL AND rejected_by IS NULL AND rejection_reason IS NULL)");
            t.HasCheckConstraint(
                "ck_journals_posted_fields",
                "(status = 'POSTED' AND posted_at IS NOT NULL AND posted_by IS NOT NULL) OR " +
                "(status <> 'POSTED' AND posted_at IS NULL AND posted_by IS NULL)");
        });

        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.Property(j => j.Currency).HasConversion(CurrencyConversion.Converter).HasColumnType("character(3)");
        builder.Property(j => j.Description).HasMaxLength(500);
        builder.Property(j => j.Type).HasColumnName("transaction_type").HasConversion(EnumText.Converter<JournalType>()).HasMaxLength(16);
        builder.Property(j => j.ExternalReference).HasMaxLength(128);
        builder.Property(j => j.Status).HasConversion(EnumText.Converter<JournalStatus>()).HasMaxLength(24);
        builder.Property(j => j.CreatedBy).HasMaxLength(128);
        builder.Property(j => j.SubmittedBy).HasMaxLength(128);
        builder.Property(j => j.ApprovedBy).HasMaxLength(128);
        builder.Property(j => j.RejectedBy).HasMaxLength(128);
        builder.Property(j => j.RejectionReason).HasMaxLength(500);
        builder.Property(j => j.PostedBy).HasMaxLength(128);
        builder.Ignore(j => j.IsReversal);

        // Target of composite foreign keys that pin entries and reversals to the same ledger and currency.
        builder.HasAlternateKey(j => new { j.Id, j.LedgerId, j.Currency });

        builder.HasOne<DomainLedger>().WithMany().HasForeignKey(j => j.LedgerId).OnDelete(DeleteBehavior.Restrict);

        // A reversal references a journal in the same ledger and currency.
        builder.HasOne<Journal>()
            .WithMany()
            .HasForeignKey(j => new { j.ReversesJournalId, j.LedgerId, j.Currency })
            .HasPrincipalKey(j => new { j.Id, j.LedgerId, j.Currency })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_journals_reversed_journal");

        builder.HasMany(j => j.Entries)
            .WithOne()
            .HasForeignKey(e => new { e.JournalId, e.LedgerId, e.Currency })
            .HasPrincipalKey(j => new { j.Id, j.LedgerId, j.Currency })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_journal_entries_journal");
        builder.Navigation(j => j.Entries).UsePropertyAccessMode(PropertyAccessMode.Field);

        // At most one live (non-rejected) reversal per journal (ADR-006).
        builder.HasIndex(j => j.ReversesJournalId)
            .IsUnique()
            .HasFilter("reverses_journal_id IS NOT NULL AND status <> 'REJECTED'")
            .HasDatabaseName(DatabaseConstraints.LiveReversalUnique);

        builder.HasIndex(j => new { j.LedgerId, j.ExternalReference })
            .IsUnique()
            .HasFilter("external_reference IS NOT NULL")
            .HasDatabaseName(DatabaseConstraints.ExternalReferenceUnique);

        builder.HasIndex(j => new { j.LedgerId, j.Status });
    }
}
