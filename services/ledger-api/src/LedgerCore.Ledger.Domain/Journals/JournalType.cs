namespace LedgerCore.Ledger.Domain.Journals;

/// <summary>
/// Business classification of a journal, sent to the policy service as contract v1
/// <c>transactionType</c> (Milestone 3). Immutable. <see cref="Reversal"/> is reserved for journals
/// created by <see cref="Journal.CreateReversal"/>.
/// </summary>
public enum JournalType
{
    Payment,
    Transfer,
    Adjustment,
    Reversal,
    Fee,
}
