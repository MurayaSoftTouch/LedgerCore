using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace LedgerCore.Ledger.Api.Tests.Infrastructure;

/// <summary>The innermost HTTP handler under the real resilience pipeline: records and answers requests.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _attempts;

    public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Received { get; } = new();

    public int Attempts => _attempts;

    public static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Received.Enqueue((request, body));
        return await respond(request, attempt).WaitAsync(cancellationToken);
    }
}
