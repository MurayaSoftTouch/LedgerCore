namespace LedgerCore.Ledger.Domain;

public enum DomainErrorKind
{
    /// <summary>The input violates a domain rule regardless of current state.</summary>
    Invalid,

    /// <summary>The operation is not allowed in the aggregate's current state.</summary>
    InvalidState,

    /// <summary>The aggregate's content breaks a ledger invariant (unbalanced, inactive account).</summary>
    RuleViolation,

    NotFound,

    /// <summary>The operation collides with existing data (duplicate code, existing reversal).</summary>
    Conflict,
}

/// <summary>
/// A violated ledger rule. <see cref="Code"/> is stable and machine-readable; the message is for humans.
/// </summary>
public sealed class LedgerDomainException(DomainErrorKind kind, string code, string message)
    : Exception(message)
{
    public DomainErrorKind Kind { get; } = kind;

    public string Code { get; } = code;
}
