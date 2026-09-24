using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LedgerCore.Ledger.Api.Persistence.Configurations;

internal sealed class OutboxEventConfiguration : IEntityTypeConfiguration<OutboxEvent>
{
    public void Configure(EntityTypeBuilder<OutboxEvent> builder)
    {
        builder.ToTable("outbox_events", t =>
            t.HasCheckConstraint("ck_outbox_events_published_after_created", "published_at IS NULL OR published_at >= created_at"));
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.AggregateType).HasMaxLength(64);
        builder.Property(e => e.EventType).HasMaxLength(64);
        builder.Property(e => e.Payload).HasColumnType("jsonb");
        // One JournalPosted per journal: a posting can only happen once, and neither can its event.
        builder.HasIndex(e => new { e.AggregateId, e.EventType }).IsUnique().HasDatabaseName("ux_outbox_events_aggregate_event");
        // What a relay would scan: unpublished events in creation order.
        builder.HasIndex(e => e.CreatedAt).HasFilter("published_at IS NULL").HasDatabaseName("ix_outbox_events_unpublished");
    }
}
