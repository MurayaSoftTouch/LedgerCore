using System.Net;
using LedgerCore.Ledger.Api.Tests.Infrastructure;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>GET /api/v1/ledgers/{id}/reconciliation.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ReconciliationApiTests(PostgresFixture db) : IDisposable
{
    private readonly LedgerApiFactory _factory = new(db);

    public void Dispose() => _factory.Dispose();

    private async Task<(ApiClient Api, Guid Ledger, Guid Journal)> ApprovedAsync()
    {
        var api = ApiClient.Create(_factory);
        var ledger = await api.CreateLedgerAsync();
        var rent = await api.OpenAccountAsync(ledger, "5000", "EXPENSE");
        var cash = await api.OpenAccountAsync(ledger, "1000", "ASSET");
        var journal = await api.CreateJournalAsync(ledger);
        await api.AddEntryAsync(ledger, journal, rent, "DEBIT", "250.00");
        await api.AddEntryAsync(ledger, journal, cash, "CREDIT", "250.00");
        await api.CommandAsync(ledger, journal, "submit");
        await api.ApproveAsync(ledger, journal);
        return (api, ledger, journal);
    }

    [Fact]
    public async Task TheReconciliationEndpointReportsAmountsAsStrings()
    {
        var (api, ledger, journal) = await ApprovedAsync();
        await ApiClient.ExpectAsync(await api.CommandAsync(ledger, journal, "post"), HttpStatusCode.OK);

        var report = await ApiClient.ExpectAsync(
            await api.Http.GetAsync(new Uri($"/api/v1/ledgers/{ledger}/reconciliation", UriKind.Relative)), HttpStatusCode.OK);

        Assert.True(report["consistent"]!.GetValue<bool>());
        Assert.Equal("HEALTHY", report["status"]!.GetValue<string>());
        Assert.NotEqual(Guid.Empty, report["runId"]!.GetValue<Guid>());
        Assert.Equal(["KES"], report["currenciesExamined"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Empty(report["discrepancies"]!.AsArray());
        Assert.False(report["discrepanciesTruncated"]!.GetValue<bool>());
        Assert.Equal(0, report["legacyPostingsWithoutClaim"]!.GetValue<int>());
        var kes = report["currencies"]![0]!;
        Assert.Equal(("KES", 1, "250.00", "250.00"), (kes["currency"]!.GetValue<string>(), kes["postedJournals"]!.GetValue<int>(), kes["debits"]!.GetValue<string>(), kes["credits"]!.GetValue<string>()));
        Assert.Contains(report["accounts"]!.AsArray(), a => a!["net"]!.GetValue<string>() == "-250.00");
        await ApiClient.ExpectProblemAsync(
            await api.Http.GetAsync(new Uri($"/api/v1/ledgers/{Guid.NewGuid()}/reconciliation", UriKind.Relative)), HttpStatusCode.NotFound, "LEDGER_NOT_FOUND");
    }
}
