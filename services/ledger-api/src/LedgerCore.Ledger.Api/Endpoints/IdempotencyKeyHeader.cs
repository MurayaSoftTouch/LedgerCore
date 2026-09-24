using LedgerCore.Ledger.Api.Application;

namespace LedgerCore.Ledger.Api.Endpoints;

/// <summary>The HTTP side of idempotent commands (ADR-015).</summary>
internal static class IdempotencyKeyHeader
{
    /// <summary>Set to <c>true</c> on a response replayed from an earlier request with the same key.</summary>
    public const string ReplayedHeader = "Idempotency-Replayed";

    public static IdempotencyKey From(HttpContext context)
    {
        var values = context.Request.Headers[IdempotencyKey.Header];
        return IdempotencyKey.Parse(values.Count > 1 ? string.Empty : values.ToString());
    }

    public static void MarkReplayed(HttpContext context, CommandResult result)
    {
        if (result.Replayed)
        {
            context.Response.Headers[ReplayedHeader] = "true";
        }
    }
}
