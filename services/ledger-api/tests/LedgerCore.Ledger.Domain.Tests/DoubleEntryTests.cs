using LedgerCore.Ledger.Domain.Journals;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Domain.Tests;

public sealed class DoubleEntryTests
{
    private readonly TestLedger _ledger = new();

    [Fact]
    public void BalancedTwoLineJournalSubmits()
    {
        var journal = _ledger.Draft((_ledger.Rent, Debit, 1000m), (_ledger.Cash, Credit, 1000m));

        journal.Submit("tester", TestLedger.Now);

        Assert.Equal(JournalStatus.PendingApproval, journal.Status);
    }

    [Fact]
    public void OneCentImbalanceIsRejected()
    {
        var journal = _ledger.Draft((_ledger.Rent, Debit, 1000.00m), (_ledger.Cash, Credit, 999.99m));

        var error = DomainAssert.Fails("JOURNAL_UNBALANCED", () => journal.Submit("tester", TestLedger.Now));
        Assert.Contains("1000.00", error.Message, StringComparison.Ordinal);
        Assert.Equal(JournalStatus.Draft, journal.Status);
    }

    [Fact]
    public void MultiEntryJournalBalancesExactly()
    {
        var journal = _ledger.Draft(
            (_ledger.Rent, Debit, 0.10m),
            (_ledger.Fees, Debit, 0.20m),
            (_ledger.Cash, Credit, 0.15m),
            (_ledger.Bank, Credit, 0.15m));

        var totals = DoubleEntry.EnsureBalanced(journal.Entries);

        // 0.1 + 0.2 == 0.3 exactly in decimal (it would not be in binary floating point).
        Assert.Equal(0.30m, totals.Debits);
        Assert.True(totals.IsBalanced);
    }

    [Fact]
    public void LargeAmountsStayExact()
    {
        const decimal big = 999_999_999_999_999_999.99m;
        var journal = _ledger.Draft(
            (_ledger.Rent, Debit, big),
            (_ledger.Cash, Credit, big - 0.01m),
            (_ledger.Bank, Credit, 0.01m));

        journal.Submit("tester", TestLedger.Now);

        Assert.Equal(JournalStatus.PendingApproval, journal.Status);
    }

    [Fact]
    public void LargeAmountsOffByOneMinorUnitAreRejected()
    {
        const decimal big = 999_999_999_999_999_999.99m;
        var journal = _ledger.Draft((_ledger.Rent, Debit, big), (_ledger.Cash, Credit, big - 0.01m));

        DomainAssert.Fails("JOURNAL_UNBALANCED", () => journal.Submit("tester", TestLedger.Now));
    }

    [Fact]
    public void JournalTotalAboveStorageRangeIsRejected()
    {
        const decimal big = 999_999_999_999_999_999.99m;
        var journal = _ledger.Draft(
            (_ledger.Rent, Debit, big),
            (_ledger.Fees, Debit, big),
            (_ledger.Cash, Credit, big),
            (_ledger.Bank, Credit, big));

        DomainAssert.Fails("JOURNAL_TOTAL_OUT_OF_RANGE", () => journal.Submit("tester", TestLedger.Now));
    }

    [Fact]
    public void EntryOrderDoesNotAffectBalance()
    {
        var lines = new (Domain.Accounts.Account, EntryDirection, decimal)[]
        {
            (_ledger.Rent, Debit, 700m),
            (_ledger.Cash, Credit, 250m),
            (_ledger.Fees, Debit, 300m),
            (_ledger.Bank, Credit, 750m),
        };

        var forward = DoubleEntry.Totals(_ledger.Draft(lines).Entries);
        var reversed = DoubleEntry.Totals(_ledger.Draft([.. lines.Reverse()]).Entries);

        Assert.Equal(forward, reversed);
        Assert.True(forward.IsBalanced);
    }

    [Fact]
    public void SameAccountMayAppearOnMultipleLines()
    {
        var journal = _ledger.Draft(
            (_ledger.Cash, Debit, 50m),
            (_ledger.Cash, Debit, 50m),
            (_ledger.Cash, Credit, 100m));

        journal.Submit("tester", TestLedger.Now);

        Assert.Equal(3, journal.Entries.Count);
        Assert.Equal(JournalStatus.PendingApproval, journal.Status);
    }

    [Fact]
    public void ManyDebitsOneCredit()
    {
        var journal = _ledger.Draft(
            (_ledger.Rent, Debit, 100m),
            (_ledger.Fees, Debit, 20.50m),
            (_ledger.Cash, Debit, 4.25m),
            (_ledger.Payables, Credit, 124.75m));

        journal.Submit("tester", TestLedger.Now);

        Assert.Equal(JournalStatus.PendingApproval, journal.Status);
    }

    [Fact]
    public void OneDebitManyCredits()
    {
        var journal = _ledger.Draft(
            (_ledger.Bank, Debit, 124.75m),
            (_ledger.Revenue, Credit, 100m),
            (_ledger.Payables, Credit, 20.50m),
            (_ledger.Cash, Credit, 4.25m));

        journal.Submit("tester", TestLedger.Now);

        Assert.Equal(JournalStatus.PendingApproval, journal.Status);
    }

    [Fact]
    public void SingleEntryIsRejected()
    {
        var journal = _ledger.Draft((_ledger.Rent, Debit, 100m));

        DomainAssert.Fails("JOURNAL_TOO_FEW_ENTRIES", () => journal.Submit("tester", TestLedger.Now));
    }

    [Fact]
    public void EmptyJournalIsRejected()
    {
        DomainAssert.Fails("JOURNAL_TOO_FEW_ENTRIES", () => _ledger.Draft().Submit("tester", TestLedger.Now));
    }

    [Fact]
    public void DebitsOnlyAreRejected()
    {
        var journal = _ledger.Draft((_ledger.Rent, Debit, 100m), (_ledger.Fees, Debit, 100m));

        DomainAssert.Fails("JOURNAL_MISSING_CREDIT", () => journal.Submit("tester", TestLedger.Now));
    }

    [Fact]
    public void CreditsOnlyAreRejected()
    {
        var journal = _ledger.Draft((_ledger.Cash, Credit, 100m), (_ledger.Bank, Credit, 100m));

        DomainAssert.Fails("JOURNAL_MISSING_DEBIT", () => journal.Submit("tester", TestLedger.Now));
    }
}
