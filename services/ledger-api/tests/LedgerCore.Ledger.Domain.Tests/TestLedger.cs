using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Journals;
using LedgerCore.Ledger.Domain.Monetary;

namespace LedgerCore.Ledger.Domain.Tests;

/// <summary>An in-memory chart of accounts for domain tests.</summary>
internal sealed class TestLedger
{
    public static readonly DateTimeOffset Now = new(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);

    public TestLedger(string currency = "KES")
    {
        Currency = Currency.FromCode(currency);
        Cash = Open("1000", AccountType.Asset);
        Bank = Open("1010", AccountType.Asset);
        Payables = Open("2000", AccountType.Liability);
        Revenue = Open("4000", AccountType.Revenue);
        Rent = Open("5000", AccountType.Expense);
        Fees = Open("5100", AccountType.Expense);
    }

    public Guid LedgerId { get; } = Guid.CreateVersion7();

    public Currency Currency { get; }

    public Account Cash { get; }

    public Account Bank { get; }

    public Account Payables { get; }

    public Account Revenue { get; }

    public Account Rent { get; }

    public Account Fees { get; }

    public Dictionary<Guid, Account> Accounts { get; } = [];

    public Account Open(string code, AccountType type, string? currency = null, Guid? ledgerId = null)
    {
        var account = Account.Open(
            ledgerId ?? LedgerId, code, $"Account {code}", type, currency is null ? Currency : Currency.FromCode(currency), Now);
        Accounts[account.Id] = account;
        return account;
    }

    public Money Amount(decimal value) => Money.Of(value, Currency);

    public Journal Draft(params (Account Account, EntryDirection Direction, decimal Amount)[] lines)
    {
        var journal = Journal.CreateDraft(LedgerId, Currency, JournalType.Payment, "test journal", null, "tester", Now);
        foreach (var (account, direction, amount) in lines)
        {
            journal.AddEntry(account, direction, Amount(amount), memo: null);
        }

        return journal;
    }

    public Journal Posted(params (Account Account, EntryDirection Direction, decimal Amount)[] lines)
    {
        var journal = Draft(lines);
        journal.Submit("tester", Now);
        journal.Approve("approver", Now);
        journal.Post(Accounts, "poster", Now);
        return journal;
    }
}
