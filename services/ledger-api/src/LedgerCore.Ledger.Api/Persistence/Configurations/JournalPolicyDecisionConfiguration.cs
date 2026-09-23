using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Domain.Journals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LedgerCore.Ledger.Api.Persistence.Configurations;

internal sealed class JournalPolicyDecisionConfiguration : IEntityTypeConfiguration<JournalPolicyDecision>
{
    public void Configure(EntityTypeBuilder<JournalPolicyDecision> builder)
    {
        builder.ToTable("journal_policy_decisions", t =>
        {
            t.HasCheckConstraint("ck_journal_policy_decisions_decision", $"decision IN ({EnumText.SqlList<PolicyDecisionValue>()})");
            t.HasCheckConstraint("ck_journal_policy_decisions_reasons", "(decision = 'APPROVED') = (cardinality(reason_codes) = 0)");
            t.HasCheckConstraint("ck_journal_policy_decisions_version", "length(policy_version) BETWEEN 1 AND 128");
        });
        builder.HasKey(d => d.JournalId);
        builder.Property(d => d.JournalId).ValueGeneratedNever();
        builder.Property(d => d.PolicyVersion).HasMaxLength(128);
        builder.Property(d => d.Decision).HasConversion(EnumText.Converter<PolicyDecisionValue>()).HasMaxLength(16);
        builder.Property(d => d.ReasonCodes).HasColumnType("text[]");
        builder.Property(d => d.ContractVersion).HasMaxLength(32);
        builder.Property(d => d.CorrelationId).HasMaxLength(128);
        builder.HasIndex(d => d.DecisionId).IsUnique().HasDatabaseName(DatabaseConstraints.PolicyDecisionUnique);
        builder.HasOne<Journal>().WithOne().HasForeignKey<JournalPolicyDecision>(d => d.JournalId).OnDelete(DeleteBehavior.Restrict);
    }
}
