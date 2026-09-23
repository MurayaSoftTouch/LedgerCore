namespace LedgerCore.Ledger.Api.Integration.Correlation;

/// <summary>Forwards the current correlation id on outbound calls (generating one if none is set).</summary>
internal sealed class CorrelationIdHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(CorrelationId.Header);
        request.Headers.TryAddWithoutValidation(CorrelationId.Header, CorrelationId.Current ??= Guid.NewGuid().ToString());
        return base.SendAsync(request, cancellationToken);
    }
}
