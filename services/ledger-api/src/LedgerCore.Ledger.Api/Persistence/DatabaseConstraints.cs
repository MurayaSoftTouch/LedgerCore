namespace LedgerCore.Ledger.Api.Persistence;

/// <summary>Constraint names the application maps to domain error codes.</summary>
internal static class DatabaseConstraints
{
    public const string LedgerCodeUnique = "ux_ledgers_code";
    public const string AccountCodeUnique = "ux_accounts_ledger_code";
    public const string ExternalReferenceUnique = "ux_journals_ledger_external_reference";
    public const string LiveReversalUnique = "ux_journals_live_reversal";

    /// <summary>SQLSTATE raised by LedgerCore triggers. The message starts with a domain error code.</summary>
    public const string LedgerInvariantSqlState = "LC001";
}
