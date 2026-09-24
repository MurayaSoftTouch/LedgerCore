using LedgerCore.Ledger.Api.Application;
using LedgerCore.Ledger.Domain.Journals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using DomainLedger = LedgerCore.Ledger.Domain.Ledgers.Ledger;

namespace LedgerCore.Ledger.Api.Persistence.Configurations;

internal sealed class CommandIdempotencyConfiguration : IEntityTypeConfiguration<CommandIdempotency>
{
    public void Configure(EntityTypeBuilder<CommandIdempotency> builder)
    {
        builder.ToTable("command_idempotency", t =>
        {
            t.HasCheckConstraint("ck_command_idempotency_operation", $"operation IN ({EnumText.SqlList<CommandOperation>()})");
            t.HasCheckConstraint("ck_command_idempotency_key", $"idempotency_key ~ '{IdempotencyKeyRules.SqlPattern}'");
            t.HasCheckConstraint("ck_command_idempotency_fingerprint", "request_fingerprint ~ '^[0-9a-f]{64}$'");
            // A posting's result is the journal it posted; a reversal's result is a different journal.
            t.HasCheckConstraint(
                "ck_command_idempotency_result",
                "(operation = 'POST_JOURNAL' AND result_journal_id = target_journal_id) OR " +
                "(operation = 'REVERSE_JOURNAL' AND result_journal_id <> target_journal_id)");
        });

        // The key is scoped by ledger and operation; this primary key decides races (ADR-015).
        builder.HasKey(c => new { c.LedgerId, c.Operation, c.IdempotencyKey }).HasName(DatabaseConstraints.IdempotencyKeyPrimaryKey);
        builder.Property(c => c.Operation).HasConversion(EnumText.Converter<CommandOperation>()).HasMaxLength(24);
        builder.Property(c => c.IdempotencyKey).HasMaxLength(IdempotencyKeyRules.MaximumLength);
        builder.Property(c => c.RequestFingerprint).HasColumnType("character(64)");
        builder.Property(c => c.RequestedBy).HasMaxLength(128);
        builder.Property(c => c.CorrelationId).HasMaxLength(128);

        builder.HasOne<DomainLedger>().WithMany().HasForeignKey(c => c.LedgerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Journal>().WithMany().HasForeignKey(c => c.TargetJournalId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_command_idempotency_target_journal");
        builder.HasOne<Journal>().WithMany().HasForeignKey(c => c.ResultJournalId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_command_idempotency_result_journal");

        // At most one posting claim per journal, and at most one claim per reversal journal.
        builder.HasIndex(c => c.TargetJournalId).IsUnique().HasFilter("operation = 'POST_JOURNAL'")
            .HasDatabaseName(DatabaseConstraints.PostingClaimUnique);
        builder.HasIndex(c => c.ResultJournalId).IsUnique().HasFilter("operation = 'REVERSE_JOURNAL'")
            .HasDatabaseName(DatabaseConstraints.ReversalClaimUnique);
    }
}
