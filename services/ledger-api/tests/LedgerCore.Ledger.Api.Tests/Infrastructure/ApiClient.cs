using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using LedgerCore.Ledger.Api.Integration.Policy;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LedgerCore.Ledger.Api.Tests.Infrastructure;

/// <summary>Thin HTTP helper for API tests. Approval uses the internal test recorder: there is no approve endpoint.</summary>
internal sealed class ApiClient(LedgerApiFactory factory, HttpClient http)
{
    public const string ActorHeader = "X-Actor-Id";

    public HttpClient Http { get; } = http;

    public static ApiClient Create(LedgerApiFactory factory) => Create(factory, factory);

    /// <summary>A client on <paramref name="host"/>, a host derived from <paramref name="factory"/> (same stub policy service).</summary>
    public static ApiClient Create(LedgerApiFactory factory, WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
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

    public async Task<Guid> CreateJournalAsync(Guid ledgerId, string? externalReference = null, string transactionType = "PAYMENT")
    {
        var response = await Http.PostAsJsonAsync(
            $"/api/v1/ledgers/{ledgerId}/journals", new { currency = "KES", description = "API journal", externalReference, transactionType });
        return (await ExpectAsync(response, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
    }

    public Task<HttpResponseMessage> AddEntryAsync(Guid ledgerId, Guid journalId, Guid accountId, string direction, object amount) =>
        Http.PostAsJsonAsync($"/api/v1/ledgers/{ledgerId}/journals/{journalId}/entries", new { accountId, direction, amount });

    /// <summary>
    /// Sends a journal command. <c>post</c> and <c>reverse</c> require an <c>Idempotency-Key</c>: a fresh
    /// one is generated unless <paramref name="idempotencyKey"/> is given (pass "" to send none).
    /// </summary>
    public async Task<HttpResponseMessage> CommandAsync(Guid ledgerId, Guid journalId, string command, string? idempotencyKey = null, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/ledgers/{ledgerId}/journals/{journalId}/{command}");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var key = idempotencyKey ?? (command is "post" or "reverse" ? Guid.NewGuid().ToString() : null);
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return await Http.SendAsync(request);
    }

    public async Task<JsonNode> GetJournalAsync(Guid ledgerId, Guid journalId) =>
        await ExpectAsync(await Http.GetAsync(new Uri($"/api/v1/ledgers/{ledgerId}/journals/{journalId}", UriKind.Relative)), HttpStatusCode.OK);

    /// <summary>
    /// Has the stub policy service decide APPROVED, then drives the real request-approval endpoint.
    /// There is no approve endpoint: approval only comes from a recorded policy decision.
    /// </summary>
    public async Task ApproveAsync(Guid ledgerId, Guid journalId)
    {
        factory.Policy.Decide(journalId, PolicyDecisionValue.Approved);
        await ExpectAsync(await CommandAsync(ledgerId, journalId, "request-approval"), HttpStatusCode.OK);
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
