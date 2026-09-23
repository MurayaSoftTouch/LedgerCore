namespace LedgerCore.Ledger.Api.Integration.Policy;

/// <summary>Contract v1 decision values. Anything else in a response is a contract violation.</summary>
internal enum PolicyDecisionValue
{
    Approved,
    Rejected,
    ReviewRequired,
}

/// <summary>A schema-valid policy decision (contract v1, <c>policy-decision-response.v1</c>).</summary>
internal sealed record PolicyDecision(
    Guid DecisionId,
    Guid TransactionId,
    string PolicyVersion,
    PolicyDecisionValue Decision,
    IReadOnlyList<string> ReasonCodes,
    DateTimeOffset EvaluatedAt,
    string ContractVersion);

/// <summary>
/// Why no trustworthy decision was obtained. None of these is a business outcome: the journal stays
/// PENDING_APPROVAL (ADR-005, ADR-012).
/// </summary>
internal enum PolicyFailureKind
{
    /// <summary>No response within the time budget (per attempt and in total).</summary>
    Timeout,

    /// <summary>Connection failure or 5xx, after bounded retries.</summary>
    Unavailable,

    /// <summary>A response that is not parseable JSON, or an unexpected status.</summary>
    InvalidResponse,

    /// <summary>A parseable response that breaks the contract (unknown decision, wrong transaction, 400, unsupported version).</summary>
    ContractViolation,

    /// <summary>409 IDEMPOTENCY_CONFLICT: the transaction was already decided with different inputs.</summary>
    Conflict,

    /// <summary>401/403: the ledger's service credential was refused. A configuration error.</summary>
    AuthenticationFailed,
}

internal abstract record PolicyEvaluationResult
{
    private PolicyEvaluationResult()
    {
    }

    internal sealed record Decided(PolicyDecision Decision) : PolicyEvaluationResult;

    internal sealed record Failed(PolicyFailureKind Kind, string Detail) : PolicyEvaluationResult;
}

internal sealed record PolicyAccountContext(Guid AccountId, string AccountType, string Side);

/// <summary>What the ledger asks the policy service (contract v1 request). Built from persisted state only.</summary>
internal sealed record PolicyDecisionRequest(
    Guid TransactionId,
    Guid OrganizationId,
    string TransactionType,
    string Currency,
    string TotalAmount,
    IReadOnlyList<PolicyAccountContext> AccountContext,
    string RequestedBy,
    DateTimeOffset RequestedAt);

internal interface IPolicyDecisionClient
{
    /// <summary>Never throws for transport or contract problems; only for caller cancellation.</summary>
    Task<PolicyEvaluationResult> EvaluateAsync(PolicyDecisionRequest request, CancellationToken cancellationToken);
}
