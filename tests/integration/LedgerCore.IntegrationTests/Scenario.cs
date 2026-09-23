using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace LedgerCore.IntegrationTests;

/// <summary>A fresh ledger with its own active policy (the policy is scoped to the ledger id).</summary>
internal sealed class Scenario
{
    public const string StandardRules = """
        [
          {"type": "AMOUNT_ABOVE", "currency": "KES", "threshold": "10000.00", "outcome": "REVIEW_REQUIRED", "reasonCode": "AMOUNT_EXCEEDS_REVIEW_THRESHOLD"},
          {"type": "AMOUNT_ABOVE", "currency": "KES", "threshold": "50000.00", "outcome": "REJECTED", "reasonCode": "AMOUNT_EXCEEDS_HARD_LIMIT"}
        ]
        """;

    private readonly Stack _stack;

    private Scenario(Stack stack, Guid ledger, Guid expense, Guid cash, string policyKey)
    {
        _stack = stack;
        Ledger = ledger;
        Expense = expense;
        Cash = cash;
        PolicyKey = policyKey;
    }

    public Guid Ledger { get; }

    public Guid Expense { get; }

    public Guid Cash { get; }

    public string PolicyKey { get; }

    public static async Task<Scenario> CreateAsync(Stack stack, string rules = StandardRules)
    {
        var ledger = Id(await Expect(await stack.Ledger.PostAsJsonAsync("api/v1/ledgers", new { code = "IT-" + Guid.NewGuid().ToString("N")[..20], name = "Integration" }), HttpStatusCode.Created));
        var expense = Id(await Expect(await stack.Ledger.PostAsJsonAsync($"api/v1/ledgers/{ledger}/accounts", new { code = "5000", name = "Expenses", type = "EXPENSE", currency = "KES" }), HttpStatusCode.Created));
        var cash = Id(await Expect(await stack.Ledger.PostAsJsonAsync($"api/v1/ledgers/{ledger}/accounts", new { code = "1000", name = "Cash", type = "ASSET", currency = "KES" }), HttpStatusCode.Created));

        // The ledger sends its ledger id as the policy organizationId (ADR-012).
        var key = "it-" + ledger.ToString("N")[..12];
        var policy = Id(await Expect(await stack.Policy.PostAsJsonAsync("api/v1/policies", new { key, name = "Integration policy", organizationId = ledger }), HttpStatusCode.Created));
        await Expect(await stack.Policy.PostAsync(new Uri($"api/v1/policies/{policy}/versions", UriKind.Relative), Json($$"""{"rules": {{rules}}}""")), HttpStatusCode.Created);
        await Expect(await stack.Policy.PostAsync(new Uri($"api/v1/policies/{policy}/versions/1/activate", UriKind.Relative), null), HttpStatusCode.OK);
        return new Scenario(stack, ledger, expense, cash, key);
    }

    public async Task<Guid> DraftAsync(string amount, string type = "PAYMENT")
    {
        var journal = Id(await Expect(await _stack.Ledger.PostAsJsonAsync($"api/v1/ledgers/{Ledger}/journals", new { currency = "KES", description = "integration", transactionType = type }), HttpStatusCode.Created));
        await Expect(await _stack.Ledger.PostAsJsonAsync($"api/v1/ledgers/{Ledger}/journals/{journal}/entries", new { accountId = Expense, direction = "DEBIT", amount }), HttpStatusCode.Created);
        await Expect(await _stack.Ledger.PostAsJsonAsync($"api/v1/ledgers/{Ledger}/journals/{journal}/entries", new { accountId = Cash, direction = "CREDIT", amount }), HttpStatusCode.Created);
        return journal;
    }

    public async Task<HttpResponseMessage> CommandAsync(Guid journal, string command, string? correlationId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/ledgers/{Ledger}/journals/{journal}/{command}");
        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-Id", correlationId);
        }

        return await _stack.Ledger.SendAsync(request);
    }

    public async Task<JsonNode> GetAsync(Guid journal) =>
        await Expect(await _stack.Ledger.GetAsync(new Uri($"api/v1/ledgers/{Ledger}/journals/{journal}", UriKind.Relative)), HttpStatusCode.OK);

    /// <summary>Decisions the policy service itself stored for this transaction (journal id).</summary>
    public Task<long> PolicyDecisionsAsync(Guid journal) =>
        ScalarAsync<long>("policy", "SELECT count(*) FROM policy_decisions WHERE transaction_id = $1", journal);

    public Task<long> LedgerEvidenceAsync(Guid journal) =>
        ScalarAsync<long>("ledger", "SELECT count(*) FROM journal_policy_decisions WHERE journal_id = $1", journal);

    public Task<long> TransitionsAsync(Guid journal, string to) =>
        ScalarAsync<long>("ledger", "SELECT count(*) FROM journal_status_transitions WHERE journal_id = $1 AND to_status = $2", journal, to);

    public async Task<T?> ScalarAsync<T>(string database, string sql, params object[] args)
    {
        await using var connection = _stack.Admin(database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public static async Task<JsonNode> Expect(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: expected {(int)status}, got {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body.Length == 0 ? "{}" : body)!;
    }

    public static async Task ExpectProblem(HttpResponseMessage response, HttpStatusCode status, string code) =>
        Assert.Equal(code, (await Expect(response, status))["code"]?.GetValue<string>());

    private static Guid Id(JsonNode node) => node["id"]!.GetValue<Guid>();

    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
