using System.Net;
using System.Net.Http.Json;
using LedgerCore.Ledger.Api.Tests.Infrastructure;

namespace LedgerCore.Ledger.Api.Tests;

[Collection(PostgresTestGroup.Name)]
public sealed class JournalApiTests(PostgresFixture db) : IDisposable
{
    private readonly LedgerApiFactory _factory = new(db);

    [Fact]
    public async Task FullLifecycleThroughTheApi()
    {
        var (api, ledger, rent, cash) = await SetUpAsync();
        var journal = await api.CreateJournalAsync(ledger);

        await ApiClient.ExpectAsync(await api.AddEntryAsync(ledger, journal, rent, "DEBIT", "1500.50"), HttpStatusCode.Created);
        var withEntries = await ApiClient.ExpectAsync(await api.AddEntryAsync(ledger, journal, cash, "CREDIT", "1500.50"), HttpStatusCode.Created);
        Assert.Equal("DRAFT", withEntries["status"]!.GetValue<string>());
        Assert.Equal("1500.50", withEntries["totals"]!["debits"]!.GetValue<string>());
        Assert.True(withEntries["totals"]!["balanced"]!.GetValue<bool>());

        var submitted = await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "submit"), HttpStatusCode.OK);
        Assert.Equal("PENDING_APPROVAL", submitted["status"]!.GetValue<string>());
        Assert.Equal("api-tester", submitted["submittedBy"]!.GetValue<string>());

        await api.ApproveAsync(ledger, journal);
        var posted = await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "post"), HttpStatusCode.OK);
        Assert.Equal("POSTED", posted["status"]!.GetValue<string>());
        Assert.False(posted["isReversed"]!.GetValue<bool>());

        await ApiClient.ExpectProblemAsync(await api.CommandAsync(ledger, journal, "post"), HttpStatusCode.Conflict, "JOURNAL_ALREADY_POSTED");
        await ApiClient.ExpectProblemAsync(
            await api.AddEntryAsync(ledger, journal, rent, "DEBIT", "1"), HttpStatusCode.Conflict, "JOURNAL_INVALID_STATE");

        var reversalResponse = await ApiClient.ExpectAsync(
            await api.Http.PostAsJsonAsync($"/api/v1/ledgers/{ledger}/journals/{journal}/reverse", new { description = "Wrong period" }),
            HttpStatusCode.Created);
        var reversal = reversalResponse["id"]!.GetValue<Guid>();
        Assert.Equal("PENDING_APPROVAL", reversalResponse["status"]!.GetValue<string>());
        Assert.Equal(journal, reversalResponse["reversesJournalId"]!.GetValue<Guid>());
        Assert.Equal(
            ["CREDIT", "DEBIT"], reversalResponse["entries"]!.AsArray().Select(e => e!["direction"]!.GetValue<string>()));

        await api.ApproveAsync(ledger, reversal);
        await ApiClient.ExpectAsync(await api.CommandAsync(ledger, reversal, "post"), HttpStatusCode.OK);

        var original = await api.GetJournalAsync(ledger, journal);
        Assert.Equal("POSTED", original["status"]!.GetValue<string>());
        Assert.True(original["isReversed"]!.GetValue<bool>());
        Assert.Equal(reversal, original["reversedByJournalId"]!.GetValue<Guid>());

        await ApiClient.ExpectProblemAsync(await api.CommandAsync(ledger, journal, "reverse"), HttpStatusCode.Conflict, "JOURNAL_ALREADY_REVERSED");
    }

    [Fact]
    public async Task UnbalancedJournalCannotBeSubmitted()
    {
        var (api, ledger, rent, cash) = await SetUpAsync();
        var journal = await api.CreateJournalAsync(ledger);
        await api.AddEntryAsync(ledger, journal, rent, "DEBIT", "100.00");
        await api.AddEntryAsync(ledger, journal, cash, "CREDIT", "99.99");

        await ApiClient.ExpectProblemAsync(await api.CommandAsync(ledger, journal, "submit"), HttpStatusCode.UnprocessableEntity, "JOURNAL_UNBALANCED");
        Assert.Equal("DRAFT", (await api.GetJournalAsync(ledger, journal))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task JournalNeedsAtLeastTwoEntries()
    {
        var (api, ledger, rent, _) = await SetUpAsync();
        var journal = await api.CreateJournalAsync(ledger);
        await api.AddEntryAsync(ledger, journal, rent, "DEBIT", "10");

        await ApiClient.ExpectProblemAsync(await api.CommandAsync(ledger, journal, "submit"), HttpStatusCode.UnprocessableEntity, "JOURNAL_TOO_FEW_ENTRIES");
    }

    [Theory]
    [InlineData("0", "AMOUNT_NOT_POSITIVE")]
    [InlineData("0.00", "AMOUNT_NOT_POSITIVE")]
    [InlineData("-5", "AMOUNT_FORMAT_INVALID")]
    [InlineData("1.001", "AMOUNT_PRECISION_EXCEEDED")]
    [InlineData("1e3", "AMOUNT_FORMAT_INVALID")]
    [InlineData("12,50", "AMOUNT_FORMAT_INVALID")]
    [InlineData("abc", "AMOUNT_FORMAT_INVALID")]
    [InlineData("1234567890123456789", "AMOUNT_FORMAT_INVALID")]
    public async Task InvalidAmountsAreRejected(string amount, string code)
    {
        var (api, ledger, rent, _) = await SetUpAsync();
        var journal = await api.CreateJournalAsync(ledger);

        await ApiClient.ExpectProblemAsync(await api.AddEntryAsync(ledger, journal, rent, "DEBIT", amount), HttpStatusCode.BadRequest, code);
    }

    [Fact]
    public async Task JsonNumberAmountIsRejected()
    {
        var (api, ledger, rent, _) = await SetUpAsync();
        var journal = await api.CreateJournalAsync(ledger);

        var response = await api.AddEntryAsync(ledger, journal, rent, "DEBIT", 10.5);

        await ApiClient.ExpectProblemAsync(response, HttpStatusCode.BadRequest, "REQUEST_INVALID");
    }

    [Fact]
    public async Task UnknownAccountIsRejected()
    {
        var (api, ledger, _, _) = await SetUpAsync();
        var journal = await api.CreateJournalAsync(ledger);

        await ApiClient.ExpectProblemAsync(
            await api.AddEntryAsync(ledger, journal, Guid.NewGuid(), "DEBIT", "10"), HttpStatusCode.UnprocessableEntity, "ACCOUNT_NOT_FOUND");
    }

    [Fact]
    public async Task AccountFromAnotherLedgerIsRejected()
    {
        var (api, ledger, _, _) = await SetUpAsync();
        var otherLedger = await api.CreateLedgerAsync();
        var foreign = await api.OpenAccountAsync(otherLedger, "1000");
        var journal = await api.CreateJournalAsync(ledger);

        await ApiClient.ExpectProblemAsync(
            await api.AddEntryAsync(ledger, journal, foreign, "DEBIT", "10"), HttpStatusCode.UnprocessableEntity, "ACCOUNT_NOT_FOUND");
    }

    [Fact]
    public async Task AccountInAnotherCurrencyIsRejected()
    {
        var (api, ledger, _, _) = await SetUpAsync();
        var usd = await api.OpenAccountAsync(ledger, "1100", currency: "USD");
        var journal = await api.CreateJournalAsync(ledger);

        await ApiClient.ExpectProblemAsync(
            await api.AddEntryAsync(ledger, journal, usd, "DEBIT", "10"), HttpStatusCode.UnprocessableEntity, "CURRENCY_MISMATCH");
    }

    [Fact]
    public async Task InactiveAccountCannotReceiveEntries()
    {
        var (api, ledger, rent, _) = await SetUpAsync();
        await api.Http.PostAsync(new Uri($"/api/v1/ledgers/{ledger}/accounts/{rent}/deactivate", UriKind.Relative), null);
        var journal = await api.CreateJournalAsync(ledger);

        await ApiClient.ExpectProblemAsync(
            await api.AddEntryAsync(ledger, journal, rent, "DEBIT", "10"), HttpStatusCode.UnprocessableEntity, "ACCOUNT_INACTIVE");
    }

    [Fact]
    public async Task PostingRequiresApproval()
    {
        var (api, ledger, rent, cash) = await SetUpAsync();
        var journal = await api.CreateJournalAsync(ledger);
        await api.AddEntryAsync(ledger, journal, rent, "DEBIT", "10");
        await api.AddEntryAsync(ledger, journal, cash, "CREDIT", "10");
        await api.CommandAsync(ledger, journal, "submit");

        await ApiClient.ExpectProblemAsync(await api.CommandAsync(ledger, journal, "post"), HttpStatusCode.Conflict, "JOURNAL_INVALID_STATE");
    }

    [Fact]
    public async Task ThereIsNoApproveEndpointInMilestone1()
    {
        var (api, ledger, rent, cash) = await SetUpAsync();
        var journal = await api.CreateJournalAsync(ledger);
        await api.AddEntryAsync(ledger, journal, rent, "DEBIT", "10");
        await api.AddEntryAsync(ledger, journal, cash, "CREDIT", "10");
        await api.CommandAsync(ledger, journal, "submit");

        Assert.Equal(HttpStatusCode.NotFound, (await api.CommandAsync(ledger, journal, "approve")).StatusCode);
    }

    [Fact]
    public async Task JournalsHaveNoGenericUpdateOrDelete()
    {
        var (api, ledger, _, _) = await SetUpAsync();
        var journal = await api.CreateJournalAsync(ledger);
        var path = new Uri($"/api/v1/ledgers/{ledger}/journals/{journal}", UriKind.Relative);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await api.Http.PatchAsJsonAsync(path, new { status = "POSTED" })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await api.Http.PutAsJsonAsync(path, new { status = "POSTED" })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await api.Http.DeleteAsync(path)).StatusCode);
        Assert.Equal("DRAFT", (await api.GetJournalAsync(ledger, journal))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task ActorHeaderIsRequiredForJournalCommands()
    {
        var (api, ledger, _, _) = await SetUpAsync();
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            $"/api/v1/ledgers/{ledger}/journals", new { currency = "KES", description = "x", transactionType = "PAYMENT" });

        await ApiClient.ExpectProblemAsync(response, HttpStatusCode.BadRequest, "ACTOR_REQUIRED");
    }

    [Fact]
    public async Task ExternalReferenceIsUniquePerLedger()
    {
        var (api, ledger, _, _) = await SetUpAsync();
        await api.CreateJournalAsync(ledger, "INV-2026-0001");

        var response = await api.Http.PostAsJsonAsync(
            $"/api/v1/ledgers/{ledger}/journals", new { currency = "KES", description = "retry", externalReference = "INV-2026-0001", transactionType = "PAYMENT" });

        await ApiClient.ExpectProblemAsync(response, HttpStatusCode.Conflict, "EXTERNAL_REFERENCE_TAKEN");
    }

    [Fact]
    public async Task UnknownJournalIsNotFound()
    {
        var (api, ledger, _, _) = await SetUpAsync();

        await ApiClient.ExpectProblemAsync(
            await api.Http.GetAsync(new Uri($"/api/v1/ledgers/{ledger}/journals/{Guid.NewGuid()}", UriKind.Relative)),
            HttpStatusCode.NotFound,
            "JOURNAL_NOT_FOUND");
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(ApiClient Api, Guid Ledger, Guid Rent, Guid Cash)> SetUpAsync()
    {
        var api = ApiClient.Create(_factory);
        var ledger = await api.CreateLedgerAsync();
        var rent = await api.OpenAccountAsync(ledger, "5000", "EXPENSE");
        var cash = await api.OpenAccountAsync(ledger, "1000", "ASSET");
        return (api, ledger, rent, cash);
    }
}
