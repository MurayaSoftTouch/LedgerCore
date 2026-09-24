using LedgerCore.Ledger.Api.Application;
using LedgerCore.Ledger.Api.Tests.Infrastructure;
using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Journals;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>The ledger reconciliation report (Milestone 4) against real PostgreSQL.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ReconciliationTests(PostgresFixture db)
{
    private async Task<ReconciliationReport> ReconcileAsync(Guid ledgerId)
    {
        await using var ctx = db.CreateRuntimeContext();
        return await new LedgerReconciliation(ctx, TimeProvider.System).RunAsync(ledgerId, default);
    }

    private static decimal Net(ReconciliationReport report, Guid account) => report.Accounts.Single(a => a.AccountId == account).Net;

    [Fact]
    public async Task PostedActivityReconcilesAndNoiseIsIgnored()
    {
        var s = await LedgerScenario.CreateAsync(db);
        await s.PostedAsync((s.Bank, Debit, 100_000m), (s.Revenue, Credit, 100_000m));
        await s.PostedAsync((s.Rent, Debit, 25_000m), (s.Payables, Credit, 25_000m));
        await s.PostedAsync((s.Payables, Debit, 25_000m), (s.Bank, Credit, 24_999.99m), (s.Cash, Credit, 0.01m));

        // Draft, pending, approved-but-unposted and rejected journals.
        await s.DraftAsync((s.Cash, Debit, 1m), (s.Revenue, Credit, 1m));
        var pending = await s.DraftAsync((s.Cash, Debit, 2m), (s.Revenue, Credit, 2m));
        await s.SubmitAsync(pending);
        await s.ApprovedAsync((s.Cash, Debit, 3m), (s.Revenue, Credit, 3m));
        var rejected = await s.DraftAsync((s.Cash, Debit, 4m), (s.Revenue, Credit, 4m));
        await s.SubmitAsync(rejected);
        await s.RejectAsync(rejected);

        var report = await ReconcileAsync(s.LedgerId);

        Assert.True(report.Consistent);
        Assert.Empty(report.UnbalancedJournals);
        Assert.Empty(report.MismatchedReversals);
        var kes = Assert.Single(report.Currencies);
        Assert.Equal(("KES", 3, 150_000m, 150_000m, true), (kes.Currency, kes.PostedJournals, kes.Debits, kes.Credits, kes.Balanced));
        Assert.Equal(75_000.01m, Net(report, s.Bank));
        Assert.Equal(-0.01m, Net(report, s.Cash));
        Assert.Equal(0m, Net(report, s.Payables));
        Assert.Equal(-100_000m, Net(report, s.Revenue));
        Assert.Equal(25_000m, Net(report, s.Rent));
        Assert.Equal(0m, report.Accounts.Sum(a => a.Net));
    }

    [Fact]
    public async Task AnOriginalAndItsReversalNetToZeroAndBothRemainInHistory()
    {
        var s = await LedgerScenario.CreateAsync(db);
        await s.PostedAsync((s.Bank, Debit, 500m), (s.Revenue, Credit, 500m));
        var before = await ReconcileAsync(s.LedgerId);

        var mistake = await s.PostedAsync((s.Rent, Debit, 3_333.33m), (s.Cash, Credit, 1_111.11m), (s.Bank, Credit, 2_222.22m));
        var reversal = await s.ReverseAsync(mistake);
        await s.ApproveAsync(reversal.Id);
        await s.PostAsync(reversal.Id);

        var after = await ReconcileAsync(s.LedgerId);

        Assert.True(after.Consistent);
        Assert.Empty(after.MismatchedReversals);
        foreach (var account in s.Accounts.Values)
        {
            Assert.Equal(Net(before, account), Net(after, account));
        }

        // Nothing is hidden: the original and the reversal are both counted, each once.
        var kes = Assert.Single(after.Currencies);
        Assert.Equal(3, kes.PostedJournals);
        Assert.Equal(500m + 3_333.33m + 3_333.33m, kes.Debits);
    }

    [Fact]
    public async Task CurrenciesAreReconciledSeparately()
    {
        var s = await LedgerScenario.CreateAsync(db);
        await s.PostedAsync((s.Bank, Debit, 10m), (s.Revenue, Credit, 10m));
        Guid usdCash, usdRevenue;
        await using (var ctx = db.CreateRuntimeContext())
        {
            var accounts = new AccountCommands(ctx, TimeProvider.System);
            usdCash = (await accounts.OpenAccountAsync(s.LedgerId, "1100", "USD cash", AccountType.Asset, "USD", default)).Id;
            usdRevenue = (await accounts.OpenAccountAsync(s.LedgerId, "4100", "USD revenue", AccountType.Revenue, "USD", default)).Id;
        }

        await using (var ctx = db.CreateRuntimeContext())
        {
            var journals = new JournalCommands(ctx, TimeProvider.System);
            var usd = await journals.CreateDraftAsync(s.LedgerId, "USD", JournalType.Payment, "usd sale", null, "tester", default);
            await journals.AddEntryAsync(s.LedgerId, usd.Id, usdCash, Debit, 7.25m, null, default);
            await journals.AddEntryAsync(s.LedgerId, usd.Id, usdRevenue, Credit, 7.25m, null, default);
            await s.SubmitAsync(usd.Id);
            await s.ApproveAsync(usd.Id);
            await s.PostAsync(usd.Id);
        }

        var report = await ReconcileAsync(s.LedgerId);

        Assert.True(report.Consistent);
        Assert.Equal(["KES", "USD"], report.Currencies.Select(c => c.Currency));
        Assert.Equal(10m, report.Currencies.Single(c => c.Currency == "KES").Debits);
        Assert.Equal(7.25m, report.Currencies.Single(c => c.Currency == "USD").Credits);
        Assert.Equal(0m, report.Accounts.Where(a => a.Currency == "USD").Sum(a => a.Net));
    }

    [Fact]
    public async Task DataThatBypassedTheGuardsIsReported()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var healthy = await s.PostedAsync((s.Bank, Debit, 10m), (s.Revenue, Credit, 10m));
        var original = await s.PostedAsync((s.Rent, Debit, 20m), (s.Cash, Credit, 20m));
        var reversal = await s.ReverseAsync(original);
        await s.ApproveAsync(reversal.Id);
        await s.PostAsync(reversal.Id);

        // As the superuser with triggers off: add an entry to one posted journal (now unbalanced) and
        // change an amount in the posted reversal (no longer a mirror).
        await using (var superuser = db.CreateSuperuserConnection())
        {
            await superuser.OpenAsync();
            await using var tx = await superuser.BeginTransactionAsync();
            await Sql.ExecuteAsync(superuser, "SET LOCAL session_replication_role = replica");
            await Sql.ExecuteAsync(
                superuser,
                "INSERT INTO journal_entries (id, journal_id, ledger_id, currency, line_number, account_id, direction, amount) VALUES (gen_random_uuid(), $1, $2, 'KES', 99, $3, 'DEBIT', 1)",
                healthy,
                s.LedgerId,
                s.Cash);
            // 20 → 18, so the two edits (+1, −2) don't cancel out in the currency totals.
            await Sql.ExecuteAsync(superuser, "UPDATE journal_entries SET amount = 18 WHERE journal_id = $1 AND direction = 'DEBIT'", reversal.Id);
            await tx.CommitAsync();
        }

        var report = await ReconcileAsync(s.LedgerId);

        Assert.False(report.Consistent);
        Assert.Contains(healthy, report.UnbalancedJournals);
        Assert.Contains(reversal.Id, report.UnbalancedJournals);
        Assert.Equal([reversal.Id], report.MismatchedReversals);
        var kes = Assert.Single(report.Currencies);
        Assert.False(kes.Balanced);
        Assert.Equal(kes.Credits - 1m, kes.Debits);
    }

    [Fact]
    public async Task AnUnknownLedgerIsNotFound()
    {
        var error = await Assert.ThrowsAsync<LedgerCore.Ledger.Domain.LedgerDomainException>(() => ReconcileAsync(Guid.NewGuid()));

        Assert.Equal("LEDGER_NOT_FOUND", error.Code);
    }
}
