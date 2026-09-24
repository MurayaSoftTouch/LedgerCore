using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace LedgerCore.Ledger.Api.Operations;

/// <summary>
/// HTTP limits (Milestone 5). No ledger request needs more than a few kilobytes: journal entries are
/// added one per request, text fields are at most 500 characters, and nothing takes an array.
/// </summary>
internal static class RequestLimits
{
    public const long MaximumBodyBytes = 64 * 1024;

    public const int MaximumJsonDepth = 16;

    /// <summary>Kestrel enforces these for every request, including chunked bodies.</summary>
    public static void Apply(KestrelServerOptions kestrel)
    {
        kestrel.AddServerHeader = false;
        kestrel.Limits.MaxRequestBodySize = MaximumBodyBytes;
        kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
        kestrel.Limits.MaxRequestHeaderCount = 50;
        kestrel.Limits.MaxRequestLineSize = 4 * 1024;
    }
}

/// <summary>
/// Refuses a declared body over <see cref="RequestLimits.MaximumBodyBytes"/> with <c>413</c> before
/// anything reads it. Kestrel's own limit is the backstop (and covers bodies without a length); this
/// middleware also applies where Kestrel is not the server (in-memory test host).
/// </summary>
internal sealed class RequestSizeLimitMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.ContentLength > RequestLimits.MaximumBodyBytes)
        {
            throw new Microsoft.AspNetCore.Http.BadHttpRequestException("The request body exceeds the allowed size.", StatusCodes.Status413PayloadTooLarge);
        }

        return next(context);
    }
}
