using LedgerCore.Ledger.Domain;

namespace LedgerCore.Ledger.Api.Application;

internal static class NotFound
{
    public static LedgerDomainException Ledger(Guid id) =>
        new(DomainErrorKind.NotFound, "LEDGER_NOT_FOUND", $"Ledger {id} does not exist.");

    public static LedgerDomainException Account(Guid id) =>
        new(DomainErrorKind.NotFound, "ACCOUNT_NOT_FOUND", $"Account {id} does not exist in this ledger.");

    public static LedgerDomainException Journal(Guid id) =>
        new(DomainErrorKind.NotFound, "JOURNAL_NOT_FOUND", $"Journal {id} does not exist in this ledger.");
}
