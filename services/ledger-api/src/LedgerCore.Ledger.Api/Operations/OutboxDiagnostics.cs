using LedgerCore.Ledger.Api.Configuration;
using LedgerCore.Ledger.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LedgerCore.Ledger.Api.Operations;

/// <summary>
/// State of unpublished outbox events. There is no publisher yet (ADR-014), so these words describe
/// waiting, not delivery: nothing has been attempted, so nothing has failed.
/// </summary>
internal enum OutboxState
{
    /// <summary>No unpublished events.</summary>
    Empty,

    /// <summary>Unpublished events, all younger than the aging threshold.</summary>
    Pending,

    /// <summary>At least one unpublished event is older than the aging threshold.</summary>
    Aging,
}

internal sealed record OutboxEventTypeStatus(string EventType, int Pending, DateTimeOffset OldestPendingAt, TimeSpan OldestPendingAge, OutboxState State);

/// <param name="GeneratedAt">When the figures were read.</param>
/// <param name="State">The worst state over all event types.</param>
/// <param name="AgingThreshold">Age after which an unpublished event is AGING (<c>Ledger:OutboxAgingThresholdSeconds</c>).</param>
/// <param name="Pending">Unpublished events.</param>
/// <param name="OldestPendingAt">Creation time of the oldest unpublished event, if any.</param>
/// <param name="OldestPendingAge">Its age, if any.</param>
/// <param name="EventTypes">The same figures per event type.</param>
/// <param name="PublisherConfigured">Always false until a relay exists (Milestone 6+).</param>
internal sealed record OutboxStatus(
    DateTimeOffset GeneratedAt,
    OutboxState State,
    TimeSpan AgingThreshold,
    int Pending,
    DateTimeOffset? OldestPendingAt,
    TimeSpan? OldestPendingAge,
    IReadOnlyList<OutboxEventTypeStatus> EventTypes,
    bool PublisherConfigured);

/// <summary>
/// Aggregates only, from the partial index on unpublished events: counts and ages per event type.
/// No payloads and no aggregate ids, so it is safe on an unauthenticated operations endpoint.
/// </summary>
internal sealed class OutboxDiagnostics(LedgerDbContext db, TimeProvider time, IOptions<LedgerApiOptions> options)
{
    public static OutboxState Classify(TimeSpan? oldestAge, TimeSpan threshold) =>
        oldestAge is null ? OutboxState.Empty : oldestAge > threshold ? OutboxState.Aging : OutboxState.Pending;

    public async Task<OutboxStatus> GetAsync(CancellationToken ct)
    {
        var threshold = TimeSpan.FromSeconds(options.Value.OutboxAgingThresholdSeconds);
        var rows = await db.OutboxEvents.AsNoTracking()
            .Where(e => e.PublishedAt == null)
            .GroupBy(e => e.EventType)
            .Select(g => new { EventType = g.Key, Pending = g.Count(), Oldest = g.Min(e => e.CreatedAt) })
            .OrderBy(g => g.EventType)
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        var types = rows
            .Select(r => new OutboxEventTypeStatus(r.EventType, r.Pending, r.Oldest, now - r.Oldest, Classify(now - r.Oldest, threshold)))
            .ToList();
        var oldest = types.Count == 0 ? (DateTimeOffset?)null : types.Min(t => t.OldestPendingAt);
        var oldestAge = now - oldest;
        return new OutboxStatus(now, Classify(oldestAge, threshold), threshold, types.Sum(t => t.Pending), oldest, oldestAge, types, PublisherConfigured: false);
    }
}
