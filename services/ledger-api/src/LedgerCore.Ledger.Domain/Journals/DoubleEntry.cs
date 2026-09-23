using LedgerCore.Ledger.Domain.Monetary;

namespace LedgerCore.Ledger.Domain.Journals;

public readonly record struct JournalTotals(decimal Debits, decimal Credits, int DebitCount, int CreditCount)
{
    /// <summary>Exact decimal equality. There is no tolerance: one minor unit off is unbalanced.</summary>
    public bool IsBalanced => Debits == Credits;
}

/// <summary>The central invariant (ADR-004): a journal balances when SUM(debits) == SUM(credits).</summary>
public static class DoubleEntry
{
    public const int MinimumEntries = 2;

    public static JournalTotals Totals(IEnumerable<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        decimal debits = 0m, credits = 0m;
        int debitCount = 0, creditCount = 0;
        foreach (var entry in entries)
        {
            if (entry.Direction == EntryDirection.Debit)
            {
                debits += entry.Amount;
                debitCount++;
            }
            else
            {
                credits += entry.Amount;
                creditCount++;
            }
        }

        return new JournalTotals(debits, credits, debitCount, creditCount);
    }

    /// <summary>Throws unless the entries form a postable double-entry journal.</summary>
    public static JournalTotals EnsureBalanced(IReadOnlyCollection<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count < MinimumEntries)
        {
            throw Invalid("JOURNAL_TOO_FEW_ENTRIES", $"A journal needs at least {MinimumEntries} entries.");
        }

        var totals = Totals(entries);
        if (totals.DebitCount == 0)
        {
            throw Invalid("JOURNAL_MISSING_DEBIT", "A journal needs at least one debit entry.");
        }

        if (totals.CreditCount == 0)
        {
            throw Invalid("JOURNAL_MISSING_CREDIT", "A journal needs at least one credit entry.");
        }

        if (!totals.IsBalanced)
        {
            throw Invalid(
                "JOURNAL_UNBALANCED", $"Debits ({totals.Debits}) do not equal credits ({totals.Credits}).");
        }

        if (totals.Debits > Money.MaxValue)
        {
            throw Invalid("JOURNAL_TOTAL_OUT_OF_RANGE", $"Journal total must not exceed {Money.MaxValue}.");
        }

        return totals;
    }

    private static LedgerDomainException Invalid(string code, string message) =>
        new(DomainErrorKind.RuleViolation, code, message);
}
