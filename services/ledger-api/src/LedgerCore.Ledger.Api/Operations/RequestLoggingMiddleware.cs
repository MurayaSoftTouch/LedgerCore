using System.Diagnostics;

namespace LedgerCore.Ledger.Api.Operations;

/// <summary>
/// One structured line per request (Milestone 5): method, route template (not the raw path, so ids
/// and query strings stay out), status and duration, inside the correlation-id scope. It never logs
/// bodies, query strings or headers. Health probes log at Debug to keep the signal readable.
/// </summary>
internal sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)";
            var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var status = context.Response.StatusCode;
            var level = route.StartsWith("/health", StringComparison.Ordinal) ? LogLevel.Debug
                : status >= 500 ? LogLevel.Warning
                : LogLevel.Information;
            LogRequest(level, context.Request.Method, route, status, durationMs);
        }
    }

    [LoggerMessage(Message = "HTTP {HttpMethod} {Route} responded {StatusCode} in {DurationMs} ms")]
    private partial void LogRequest(LogLevel level, string httpMethod, string route, int statusCode, long durationMs);
}
