namespace LedgerCore.Ledger.Api.Integration.Correlation;

/// <summary>
/// Accepts a valid inbound <c>X-Correlation-Id</c> or generates one, echoes it on the response, and
/// scopes every log line of the request with it.
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = CorrelationId.AcceptOrCreate(context.Request.Headers[CorrelationId.Header].ToString());
        CorrelationId.Current = correlationId;
        context.Response.Headers[CorrelationId.Header] = correlationId;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}
