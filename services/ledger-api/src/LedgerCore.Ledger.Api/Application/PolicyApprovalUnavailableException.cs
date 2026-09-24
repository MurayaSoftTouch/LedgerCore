using LedgerCore.Ledger.Api.Integration.Policy;

namespace LedgerCore.Ledger.Api.Application;

/// <summary>Raised by the request-approval command when no trustworthy decision was obtained.</summary>
internal sealed class PolicyApprovalUnavailableException(PolicyFailureKind failure)
    : Exception($"No trustworthy policy decision was obtained ({failure}).")
{
    public PolicyFailureKind Failure { get; } = failure;
}
