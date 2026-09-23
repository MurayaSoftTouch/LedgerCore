namespace LedgerCore.Ledger.Domain.Journals;

/// <summary>Side of a journal entry. Amounts are always positive; direction carries the sign.</summary>
public enum EntryDirection
{
    Debit,
    Credit,
}

public static class EntryDirectionExtensions
{
    public static EntryDirection Opposite(this EntryDirection direction) =>
        direction == EntryDirection.Debit ? EntryDirection.Credit : EntryDirection.Debit;
}
