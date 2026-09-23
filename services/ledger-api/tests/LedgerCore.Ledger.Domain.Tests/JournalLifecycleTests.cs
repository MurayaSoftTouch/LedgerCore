using LedgerCore.Ledger.Domain.Journals;
using LedgerCore.Ledger.Domain.Monetary;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Domain.Tests;

public sealed class JournalLifecycleTests
{
    private readonly TestLedger _ledger = new();

    private Journal Balanced() => _ledger.Draft((_ledger.Rent, Debit, 10m), (_ledger.Cash, Credit, 10m));

    [Fact]
    public void DraftRecordsCreator()
    {
        var journal = Balanced();

        Assert.Equal(JournalStatus.Draft, journal.Status);
        Assert.Equal("tester", journal.CreatedBy);
        Assert.Equal([1, 2], journal.Entries.Select(e => e.LineNumber));
        Assert.All(journal.Entries, e => Assert.Equal(journal.Id, e.JournalId));
    }

    [Fact]
    public void HappyPathRecordsEveryTransition()
    {
        var journal = Balanced();

        journal.Submit("alice", TestLedger.Now);
        journal.Approve("bob", TestLedger.Now.AddMinutes(1));
        var totals = journal.Post(_ledger.Accounts, "carol", TestLedger.Now.AddMinutes(2));

        Assert.Equal(JournalStatus.Posted, journal.Status);
        Assert.Equal(("alice", "bob", "carol"), (journal.SubmittedBy, journal.ApprovedBy, journal.PostedBy));
        Assert.Equal(TestLedger.Now.AddMinutes(2), journal.PostedAt);
        Assert.Equal(10m, totals.Debits);
    }

    [Fact]
    public void RejectionIsTerminal()
    {
        var journal = Balanced();
        journal.Submit("alice", TestLedger.Now);

        journal.Reject("policy", "AMOUNT_LIMIT", TestLedger.Now);

        Assert.Equal(JournalStatus.Rejected, journal.Status);
        Assert.Equal("AMOUNT_LIMIT", journal.RejectionReason);
        DomainAssert.Fails("JOURNAL_INVALID_STATE", () => journal.Approve("bob", TestLedger.Now));
        DomainAssert.Fails("JOURNAL_INVALID_STATE", () => journal.Post(_ledger.Accounts, "carol", TestLedger.Now));
    }

    [Fact]
    public void CannotApproveADraft()
    {
        DomainAssert.Fails("JOURNAL_INVALID_STATE", () => Balanced().Approve("bob", TestLedger.Now));
    }

    [Fact]
    public void CannotPostWithoutApproval()
    {
        var journal = Balanced();
        journal.Submit("alice", TestLedger.Now);

        DomainAssert.Fails("JOURNAL_INVALID_STATE", () => journal.Post(_ledger.Accounts, "carol", TestLedger.Now));
        Assert.Equal(JournalStatus.PendingApproval, journal.Status);
    }

    [Fact]
    public void PostingTwiceIsRejected()
    {
        var journal = _ledger.Posted((_ledger.Rent, Debit, 10m), (_ledger.Cash, Credit, 10m));
        var postedAt = journal.PostedAt;

        DomainAssert.Fails("JOURNAL_ALREADY_POSTED", () => journal.Post(_ledger.Accounts, "again", TestLedger.Now.AddDays(1)));
        Assert.Equal(postedAt, journal.PostedAt);
    }

    [Fact]
    public void EntriesAreFrozenAfterSubmit()
    {
        var journal = Balanced();
        journal.Submit("alice", TestLedger.Now);

        DomainAssert.Fails(
            "JOURNAL_INVALID_STATE", () => journal.AddEntry(_ledger.Fees, Debit, _ledger.Amount(1m), null));
    }

    [Fact]
    public void PostingRechecksAccountsAndRejectsInactiveAccount()
    {
        var journal = Balanced();
        journal.Submit("alice", TestLedger.Now);
        journal.Approve("bob", TestLedger.Now);

        _ledger.Rent.Deactivate(TestLedger.Now);

        DomainAssert.Fails("ACCOUNT_INACTIVE", () => journal.Post(_ledger.Accounts, "carol", TestLedger.Now));
        Assert.Equal(JournalStatus.Approved, journal.Status);
    }

    [Fact]
    public void PostingRejectsMissingAccount()
    {
        var journal = Balanced();
        journal.Submit("alice", TestLedger.Now);
        journal.Approve("bob", TestLedger.Now);
        _ledger.Accounts.Remove(_ledger.Cash.Id);

        DomainAssert.Fails("ACCOUNT_NOT_FOUND", () => journal.Post(_ledger.Accounts, "carol", TestLedger.Now));
    }

    [Fact]
    public void InactiveAccountCannotBeAddedToDraft()
    {
        _ledger.Fees.Deactivate(TestLedger.Now);

        DomainAssert.Fails("ACCOUNT_INACTIVE", () => _ledger.Draft((_ledger.Fees, Debit, 1m)));
    }

    [Fact]
    public void AccountFromAnotherLedgerIsRejected()
    {
        var foreign = _ledger.Open("9000", Domain.Accounts.AccountType.Asset, ledgerId: Guid.CreateVersion7());

        DomainAssert.Fails("ACCOUNT_NOT_IN_LEDGER", () => _ledger.Draft((foreign, Debit, 1m)));
    }

    [Fact]
    public void AccountInAnotherCurrencyIsRejected()
    {
        var usd = _ledger.Open("1100", Domain.Accounts.AccountType.Asset, currency: "USD");

        DomainAssert.Fails("CURRENCY_MISMATCH", () => _ledger.Draft((usd, Debit, 1m)));
    }

    [Fact]
    public void AmountInAnotherCurrencyIsRejected()
    {
        var journal = _ledger.Draft();

        DomainAssert.Fails(
            "CURRENCY_MISMATCH",
            () => journal.AddEntry(_ledger.Cash, Debit, Money.Of(1m, Currency.FromCode("USD")), null));
    }

    [Fact]
    public void UndefinedDirectionIsRejected()
    {
        DomainAssert.Fails("ENUM_INVALID", () => _ledger.Draft((_ledger.Cash, (EntryDirection)7, 1m)));
    }

    [Fact]
    public void JournalEntryCountIsBounded()
    {
        var journal = _ledger.Draft();
        for (var i = 0; i < Journal.MaximumEntries; i++)
        {
            journal.AddEntry(_ledger.Cash, i % 2 == 0 ? Debit : Credit, _ledger.Amount(1m), null);
        }

        DomainAssert.Fails(
            "JOURNAL_TOO_MANY_ENTRIES", () => journal.AddEntry(_ledger.Cash, Debit, _ledger.Amount(1m), null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ActorIsRequired(string actor)
    {
        var journal = Balanced();

        DomainAssert.Fails("FIELD_REQUIRED", () => journal.Submit(actor, TestLedger.Now));
        Assert.Equal(JournalStatus.Draft, journal.Status);
    }

    [Fact]
    public void InvalidInputNeverLeavesAPartialTransition()
    {
        var journal = Balanced();
        journal.Submit("alice", TestLedger.Now);

        DomainAssert.Fails("FIELD_REQUIRED", () => journal.Reject("policy", " ", TestLedger.Now));
        DomainAssert.Fails("FIELD_REQUIRED", () => journal.Approve("", TestLedger.Now));
        Assert.Equal(JournalStatus.PendingApproval, journal.Status);
        Assert.Null(journal.ApprovedAt);
        Assert.Null(journal.RejectedAt);

        journal.Approve("bob", TestLedger.Now);
        DomainAssert.Fails("FIELD_REQUIRED", () => journal.Post(_ledger.Accounts, "", TestLedger.Now));
        Assert.Equal(JournalStatus.Approved, journal.Status);
        Assert.Null(journal.PostedAt);
    }
}
