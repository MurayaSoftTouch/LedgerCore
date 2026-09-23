using LedgerCore.Ledger.Domain.Journals;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Domain.Tests;

public sealed class ReversalTests
{
    private readonly TestLedger _ledger = new();

    private Journal PostedOriginal() => _ledger.Posted(
        (_ledger.Rent, Debit, 700m),
        (_ledger.Fees, Debit, 50.25m),
        (_ledger.Cash, Credit, 750.25m));

    [Fact]
    public void ReversalMirrorsEveryEntryWithDirectionSwapped()
    {
        var original = PostedOriginal();

        var reversal = Journal.CreateReversal(original, _ledger.Accounts, null, "alice", TestLedger.Now);

        Assert.Equal(original.Id, reversal.ReversesJournalId);
        Assert.NotEqual(original.Id, reversal.Id);
        Assert.Equal(
            original.Entries.Select(e => (e.LineNumber, e.AccountId, e.Direction.Opposite(), e.Amount)),
            reversal.Entries.Select(e => (e.LineNumber, e.AccountId, e.Direction, e.Amount)));
        Assert.All(reversal.Entries, e => Assert.NotEqual(original.Entries[0].Id, e.Id));
    }

    [Fact]
    public void ReversalIsASealedBalancedDraftReadyToSubmit()
    {
        var reversal = Journal.CreateReversal(PostedOriginal(), _ledger.Accounts, null, "alice", TestLedger.Now);

        Assert.Equal(JournalStatus.Draft, reversal.Status);
        Assert.True(DoubleEntry.Totals(reversal.Entries).IsBalanced);
        DomainAssert.Fails(
            "REVERSAL_ENTRIES_FIXED", () => reversal.AddEntry(_ledger.Cash, Debit, _ledger.Amount(1m), null));

        reversal.Submit("alice", TestLedger.Now);
        Assert.Equal(JournalStatus.PendingApproval, reversal.Status);
    }

    [Fact]
    public void OriginalIsUnchanged()
    {
        var original = PostedOriginal();
        var before = Snapshot(original);

        _ = Journal.CreateReversal(original, _ledger.Accounts, null, "alice", TestLedger.Now.AddDays(1));

        Assert.Equal(before, Snapshot(original));
    }

    [Fact]
    public void ReversalNetsAccountMovementToZero()
    {
        var original = PostedOriginal();
        var reversal = Journal.CreateReversal(original, _ledger.Accounts, null, "alice", TestLedger.Now);
        reversal.Submit("alice", TestLedger.Now);
        reversal.Approve("bob", TestLedger.Now);
        reversal.Post(_ledger.Accounts, "carol", TestLedger.Now);

        var net = original.Entries.Concat(reversal.Entries)
            .GroupBy(e => e.AccountId)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Direction == Debit ? e.Amount : -e.Amount));

        Assert.All(net.Values, v => Assert.Equal(0m, v));
    }

    [Theory]
    [InlineData(JournalStatus.Draft)]
    [InlineData(JournalStatus.PendingApproval)]
    [InlineData(JournalStatus.Approved)]
    [InlineData(JournalStatus.Rejected)]
    public void OnlyPostedJournalsCanBeReversed(JournalStatus status)
    {
        var journal = _ledger.Draft((_ledger.Rent, Debit, 1m), (_ledger.Cash, Credit, 1m));
        if (status != JournalStatus.Draft)
        {
            journal.Submit("a", TestLedger.Now);
        }

        if (status == JournalStatus.Approved)
        {
            journal.Approve("b", TestLedger.Now);
        }
        else if (status == JournalStatus.Rejected)
        {
            journal.Reject("b", "NO", TestLedger.Now);
        }

        Assert.Equal(status, journal.Status);
        DomainAssert.Fails(
            "JOURNAL_NOT_POSTED", () => Journal.CreateReversal(journal, _ledger.Accounts, null, "alice", TestLedger.Now));
    }

    [Fact]
    public void ReversalCannotBeReversed()
    {
        var reversal = Journal.CreateReversal(PostedOriginal(), _ledger.Accounts, null, "alice", TestLedger.Now);
        reversal.Submit("alice", TestLedger.Now);
        reversal.Approve("bob", TestLedger.Now);
        reversal.Post(_ledger.Accounts, "carol", TestLedger.Now);

        DomainAssert.Fails(
            "REVERSAL_OF_REVERSAL", () => Journal.CreateReversal(reversal, _ledger.Accounts, null, "alice", TestLedger.Now));
    }

    [Fact]
    public void ReversalRejectsInactiveAccountUpFront()
    {
        var original = PostedOriginal();
        _ledger.Rent.Deactivate(TestLedger.Now);

        DomainAssert.Fails(
            "ACCOUNT_INACTIVE", () => Journal.CreateReversal(original, _ledger.Accounts, null, "alice", TestLedger.Now));
    }

    private static object Snapshot(Journal j) => (
        j.Status,
        j.PostedAt,
        j.PostedBy,
        j.ReversesJournalId,
        string.Join('|', j.Entries.Select(e => $"{e.Id}:{e.AccountId}:{e.Direction}:{e.Amount}")));

    [Fact]
    public void ReversalIsTypedReversalAndDraftsCannotClaimThatType()
    {
        var reversal = Journal.CreateReversal(PostedOriginal(), _ledger.Accounts, null, "alice", TestLedger.Now);

        Assert.Equal(JournalType.Reversal, reversal.Type);
        DomainAssert.Fails(
            "JOURNAL_TYPE_RESERVED",
            () => Journal.CreateDraft(_ledger.LedgerId, _ledger.Currency, JournalType.Reversal, "x", null, "a", TestLedger.Now));
        DomainAssert.Fails(
            "ENUM_INVALID",
            () => Journal.CreateDraft(_ledger.LedgerId, _ledger.Currency, (JournalType)42, "x", null, "a", TestLedger.Now));
    }
}
