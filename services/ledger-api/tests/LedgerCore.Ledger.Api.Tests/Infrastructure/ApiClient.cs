using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LedgerCore.Ledger.Api.Application;
using LedgerCore.Ledger.Api.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace LedgerCore.Ledger.Api.Tests.Infrastructure;

/// <summary>Thin HTTP helper for API tests. Approval uses the internal test recorder: there is no approve endpoint.</summary>
internal sealed class ApiClient(LedgerApiFactory factory, HttpClient http)
{
    public const string ActorHeader = "X-Actor-Id";

    public HttpClient Http { get; } = http;

    public static ApiClient Create(LedgerApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ActorHeader, "api-tester");
        return new ApiClient(factory, client);
    }

    public async Task<Guid> CreateLedgerAsync()
    {
        var response = await Http.PostAsJsonAsync("/api/v1/ledgers", new { code = $"API-{Guid.NewGuid():N}"[..32], name = "API ledger" });
        return (await ExpectAsync(response, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
    }

    public async Task<Guid> OpenAccountAsync(Guid ledgerId, string code, string type = "ASSET", string currency = "KES")
    {
        var response = await Http.PostAsJsonAsync($"/api/v1/ledgers/{ledgerId}/accounts", new { code, name = $"Account {code}", type, currency });
        return (await ExpectAsync(response, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
    }

    public async Task<Guid> CreateJournalAsync(Guid ledgerId, string? externalReference = null)
    {
        var response = await Http.PostAsJsonAsync(
            $"/api/v1/ledgers/{ledgerId}/journals", new { currency = "KES", description = "API journal", externalReference });
        return (await ExpectAsync(response, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
    }

    public Task<HttpResponseMessage> AddEntryAsync(Guid ledgerId, Guid journalId, Guid accountId, string direction, object amount) =>
        Http.PostAsJsonAsync($"/api/v1/ledgers/{ledgerId}/journals/{journalId}/entries", new { accountId, direction, amount });

    public Task<HttpResponseMessage> CommandAsync(Guid ledgerId, Guid journalId, string command) =>
        Http.PostAsync(new Uri($"/api/v1/ledgers/{ledgerId}/journals/{journalId}/{command}", UriKind.Relative), null);

    public async Task<JsonNode> GetJournalAsync(Guid ledgerId, Guid journalId) =>
        await ExpectAsync(await Http.GetAsync(new Uri($"/api/v1/ledgers/{ledgerId}/journals/{journalId}", UriKind.Relative)), HttpStatusCode.OK);

    public async Task ApproveAsync(Guid ledgerId, Guid journalId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        await new TestApprovalRecorder(db, TimeProvider.System).ApproveAsync(ledgerId, journalId, "test-approver");
    }

    public static async Task<JsonNode> ExpectAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body.Length == 0 ? "{}" : body)!;
    }

    public static async Task ExpectProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var problem = await ExpectAsync(response, status);
        Assert.Equal(code, problem["code"]?.GetValue<string>());
    }

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}
