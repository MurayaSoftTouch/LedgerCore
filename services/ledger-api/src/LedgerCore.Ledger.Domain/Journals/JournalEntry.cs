using LedgerCore.Ledger.Domain.Monetary;

namespace LedgerCore.Ledger.Domain.Journals;

/// <summary>
/// One line of a journal. <see cref="LedgerId"/> and <see cref="Currency"/> repeat the journal's
/// values so the database can enforce, with composite foreign keys, that every line uses an
/// account of the same ledger and currency.
/// </summary>
public sealed class JournalEntry
{
    private JournalEntry()
    {
    }

    internal JournalEntry(
        Journal journal, int lineNumber, Guid accountId, EntryDirection direction, Money amount, string? memo)
    {
        Id = Guid.CreateVersion7();
        JournalId = journal.Id;
        LedgerId = journal.LedgerId;
        Currency = journal.Currency;
        LineNumber = lineNumber;
        AccountId = accountId;
        Direction = Guard.Defined(direction, "direction");
        Amount = amount.Amount;
        Memo = Guard.OptionalText(memo, "memo", 500);
    }

    public Guid Id { get; private set; }

    public Guid JournalId { get; private set; }

    public Guid LedgerId { get; private set; }

    public Currency Currency { get; private set; } = null!;

    /// <summary>1-based position within the journal; stable ordering for display and reversal.</summary>
    public int LineNumber { get; private set; }

    public Guid AccountId { get; private set; }

    public EntryDirection Direction { get; private set; }

    /// <summary>Strictly positive; see <see cref="Money"/> for precision rules.</summary>
    public decimal Amount { get; private set; }

    public string? Memo { get; private set; }
}
