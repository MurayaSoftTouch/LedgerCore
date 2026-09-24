using LedgerCore.Ledger.Api.Operations;
using LedgerCore.Ledger.Api.Persistence;

namespace LedgerCore.Ledger.Api.Endpoints;

internal sealed record OutboxEventTypeResponse(string EventType, int Pending, DateTimeOffset OldestPendingAt, long OldestPendingAgeSeconds, string State);

/// <summary>Outbox waiting state. Aggregates only: no payloads, no aggregate ids.</summary>
internal sealed record OutboxStatusResponse(
    DateTimeOffset GeneratedAt,
    string State,
    long AgingThresholdSeconds,
    int Pending,
    DateTimeOffset? OldestPendingAt,
    long? OldestPendingAgeSeconds,
    IReadOnlyList<OutboxEventTypeResponse> EventTypes,
    bool PublisherConfigured)
{
    public static OutboxStatusResponse From(OutboxStatus s) => new(
        s.GeneratedAt,
        EnumText.ToText(s.State),
        (long)s.AgingThreshold.TotalSeconds,
        s.Pending,
        s.OldestPendingAt,
        s.OldestPendingAge is { } age ? (long)age.TotalSeconds : null,
        [.. s.EventTypes.Select(t => new OutboxEventTypeResponse(t.EventType, t.Pending, t.OldestPendingAt, (long)t.OldestPendingAge.TotalSeconds, EnumText.ToText(t.State)))],
        s.PublisherConfigured);
}

/// <summary>Operational diagnostics (Milestone 5), outside the business API.</summary>
internal static class OperationsEndpoints
{
    public static void MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var ops = app.MapGroup("/ops").WithTags("operations");

        // How many events wait for the (future) relay, and for how long. Nothing has failed: there is
        // no publisher, so AGING only means "older than the threshold" (ADR-014, ADR-016).
        ops.MapGet("/outbox", async (OutboxDiagnostics diagnostics, CancellationToken ct) =>
            OutboxStatusResponse.From(await diagnostics.GetAsync(ct)));
    }
}
