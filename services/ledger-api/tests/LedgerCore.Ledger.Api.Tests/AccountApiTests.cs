using System.Net;
using System.Net.Http.Json;
using LedgerCore.Ledger.Api.Tests.Infrastructure;

namespace LedgerCore.Ledger.Api.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class AccountApiTests(PostgresFixture db) : IDisposable
{
    private readonly LedgerApiFactory _factory = new(db);

    [Fact]
    public async Task CreatesAndListsAccounts()
    {
        var api = ApiClient.Create(_factory);
        var ledger = await api.CreateLedgerAsync();

        var response = await api.Http.PostAsJsonAsync(
            $"/api/v1/ledgers/{ledger}/accounts", new { code = "1000", name = "Cash", type = "ASSET", currency = "KES" });

        var account = await ApiClient.ExpectAsync(response, HttpStatusCode.Created);
        Assert.Equal("ASSET", account["type"]!.GetValue<string>());
        Assert.Equal("KES", account["currency"]!.GetValue<string>());
        Assert.True(account["isActive"]!.GetValue<bool>());
        var list = await ApiClient.ExpectAsync(await api.Http.GetAsync(new Uri($"/api/v1/ledgers/{ledger}/accounts", UriKind.Relative)), HttpStatusCode.OK);
        Assert.Single(list.AsArray());
    }

    [Fact]
    public async Task DuplicateCodeInSameLedgerIsConflict()
    {
        var api = ApiClient.Create(_factory);
        var ledger = await api.CreateLedgerAsync();
        await api.OpenAccountAsync(ledger, "1000");

        var response = await api.Http.PostAsJsonAsync(
            $"/api/v1/ledgers/{ledger}/accounts", new { code = "1000", name = "Other", type = "EXPENSE", currency = "KES" });

        await ApiClient.ExpectProblemAsync(response, HttpStatusCode.Conflict, "ACCOUNT_CODE_TAKEN");
    }

    [Fact]
    public async Task SameCodeInAnotherLedgerIsAllowed()
    {
        var api = ApiClient.Create(_factory);
        await api.OpenAccountAsync(await api.CreateLedgerAsync(), "1000");

        await api.OpenAccountAsync(await api.CreateLedgerAsync(), "1000");
    }

    // Enum names are matched case-insensitively on input ("asset" is ASSET) and always written upper case.
    [Theory]
    [InlineData("ASSETS")]
    [InlineData("CASH")]
    [InlineData("0")]
    public async Task InvalidAccountTypeIsBadRequest(string type)
    {
        var api = ApiClient.Create(_factory);
        var ledger = await api.CreateLedgerAsync();

        var response = await api.Http.PostAsJsonAsync(
            $"/api/v1/ledgers/{ledger}/accounts", new { code = "1000", name = "Cash", type, currency = "KES" });

        await ApiClient.ExpectProblemAsync(response, HttpStatusCode.BadRequest, "REQUEST_INVALID");
    }

    [Fact]
    public async Task NumericAccountTypeIsBadRequest()
    {
        var api = ApiClient.Create(_factory);
        var ledger = await api.CreateLedgerAsync();

        var response = await api.Http.PostAsJsonAsync(
            $"/api/v1/ledgers/{ledger}/accounts", new { code = "1000", name = "Cash", type = 0, currency = "KES" });

        await ApiClient.ExpectProblemAsync(response, HttpStatusCode.BadRequest, "REQUEST_INVALID");
    }

    [Fact]
    public async Task UnsupportedCurrencyIsBadRequest()
    {
        var api = ApiClient.Create(_factory);
        var ledger = await api.CreateLedgerAsync();

        var response = await api.Http.PostAsJsonAsync(
            $"/api/v1/ledgers/{ledger}/accounts", new { code = "1000", name = "Cash", type = "ASSET", currency = "XYZ" });

        await ApiClient.ExpectProblemAsync(response, HttpStatusCode.BadRequest, "CURRENCY_UNSUPPORTED");
    }

    [Fact]
    public async Task UnknownLedgerIsNotFound()
    {
        var api = ApiClient.Create(_factory);

        var response = await api.Http.PostAsJsonAsync(
            $"/api/v1/ledgers/{Guid.NewGuid()}/accounts", new { code = "1000", name = "Cash", type = "ASSET", currency = "KES" });

        await ApiClient.ExpectProblemAsync(response, HttpStatusCode.NotFound, "LEDGER_NOT_FOUND");
    }

    [Fact]
    public async Task DeactivationIsExplicitAndOneWay()
    {
        var api = ApiClient.Create(_factory);
        var ledger = await api.CreateLedgerAsync();
        var account = await api.OpenAccountAsync(ledger, "1000");
        var path = new Uri($"/api/v1/ledgers/{ledger}/accounts/{account}/deactivate", UriKind.Relative);

        var deactivated = await ApiClient.ExpectAsync(await api.Http.PostAsync(path, null), HttpStatusCode.OK);
        Assert.False(deactivated["isActive"]!.GetValue<bool>());

        await ApiClient.ExpectProblemAsync(await api.Http.PostAsync(path, null), HttpStatusCode.Conflict, "ACCOUNT_ALREADY_INACTIVE");
    }

    [Fact]
    public async Task AccountsHaveNoDeleteOrUpdateEndpoint()
    {
        var api = ApiClient.Create(_factory);
        var ledger = await api.CreateLedgerAsync();
        var account = await api.OpenAccountAsync(ledger, "1000");
        var path = new Uri($"/api/v1/ledgers/{ledger}/accounts/{account}", UriKind.Relative);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await api.Http.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await api.Http.PatchAsJsonAsync(path, new { type = "EXPENSE" })).StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
