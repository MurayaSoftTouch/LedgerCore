using LedgerCore.Ledger.Domain.Journals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LedgerCore.Ledger.Api.Persistence.Configurations;

internal sealed class JournalStatusTransitionConfiguration : IEntityTypeConfiguration<JournalStatusTransition>
{
    public void Configure(EntityTypeBuilder<JournalStatusTransition> builder)
    {
        builder.ToTable("journal_status_transitions");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).UseIdentityAlwaysColumn();
        builder.Property(t => t.FromStatus).HasConversion(EnumText.Converter<JournalStatus>()).HasMaxLength(24);
        builder.Property(t => t.ToStatus).HasConversion(EnumText.Converter<JournalStatus>()).HasMaxLength(24);
        builder.Property(t => t.Actor).HasMaxLength(128);
        builder.Property(t => t.Reason).HasMaxLength(500);
        builder.HasOne<Journal>().WithMany().HasForeignKey(t => t.JournalId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => new { t.JournalId, t.Id });
    }
}
