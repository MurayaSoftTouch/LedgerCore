using System.Net;
using System.Text.Json;
using LedgerCore.Ledger.Api.Tests.Infrastructure;

namespace LedgerCore.Ledger.Api.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class HostEndpointsTests(PostgresFixture db) : IDisposable
{
    private readonly LedgerApiFactory _factory = new(db);

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpointsReportHealthy(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenApiDocumentIsServedInDevelopment()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.TryGetProperty("openapi", out _));
        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/ledgers/{ledgerId}/journals/{journalId}/post", out _));
    }

    public void Dispose() => _factory.Dispose();
}
