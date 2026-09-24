using LedgerCore.Ledger.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LedgerCore.Ledger.Api.Operations;

/// <summary>
/// Readiness (Milestone 5, ADR-016): the ledger database has every migration this build was compiled
/// with. A missing or partly migrated schema makes the ledger unready: its guards (triggers,
/// constraints, grants) are part of correctness, so serving without them is not safe. A database
/// that is <em>ahead</em> of this build (a rolling deployment) is <c>Degraded</c>, which stays ready.
/// The runtime role only reads the migrations history; it never migrates.
/// </summary>
internal sealed class LedgerSchemaHealthCheck(LedgerDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        IReadOnlyCollection<string> applied;
        try
        {
            applied = [.. await db.Database.GetAppliedMigrationsAsync(cancellationToken)];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("ledger schema unavailable");
        }

        var expected = db.Database.GetMigrations().ToList();
        var missing = expected.Except(applied).Count();
        if (missing > 0)
        {
            return HealthCheckResult.Unhealthy($"{missing} ledger migration(s) not applied");
        }

        var unknown = applied.Except(expected).Count();
        return unknown > 0
            ? HealthCheckResult.Degraded($"database has {unknown} migration(s) newer than this build")
            : HealthCheckResult.Healthy("ledger schema current");
    }
}
