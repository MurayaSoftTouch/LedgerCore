using System.Collections.Concurrent;
using LedgerCore.Ledger.Api.Integration.Policy;

namespace LedgerCore.Ledger.Api.Tests.Infrastructure;

/// <summary>
/// In-process stand-in for the policy service, used by ledger tests that are not about the HTTP
/// client. Like the real service it returns the same decision (same decisionId) every time a
/// transaction is re-evaluated. Unconfigured transactions fail as UNAVAILABLE, so journals stay
/// PENDING_APPROVAL until a test decides them.
/// </summary>
internal sealed class StubPolicyDecisionClient : IPolicyDecisionClient
{
    private readonly ConcurrentDictionary<Guid, Func<PolicyDecisionRequest, Task<PolicyEvaluationResult>>> _responses = new();

    public ConcurrentQueue<PolicyDecisionRequest> Requests { get; } = new();

    public void Decide(Guid transactionId, PolicyDecisionValue decision, params string[] reasonCodes)
    {
        var fixedDecision = new PolicyDecision(
            Guid.NewGuid(),
            transactionId,
            "stub-policy@1",
            decision,
            decision == PolicyDecisionValue.Approved ? [] : reasonCodes.Length > 0 ? reasonCodes : ["STUB_REASON"],
            DateTimeOffset.UtcNow,
            "1.1.0");
        _responses[transactionId] = _ => Task.FromResult<PolicyEvaluationResult>(new PolicyEvaluationResult.Decided(fixedDecision));
    }

    public void Fail(Guid transactionId, PolicyFailureKind kind) =>
        _responses[transactionId] = _ => Task.FromResult<PolicyEvaluationResult>(new PolicyEvaluationResult.Failed(kind, "stubbed failure"));

    public void Respond(Guid transactionId, Func<PolicyDecisionRequest, Task<PolicyEvaluationResult>> response) =>
        _responses[transactionId] = response;

    public int CallsFor(Guid transactionId) => Requests.Count(r => r.TransactionId == transactionId);

    public Task<PolicyEvaluationResult> EvaluateAsync(PolicyDecisionRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return _responses.TryGetValue(request.TransactionId, out var response)
            ? response(request)
            : Task.FromResult<PolicyEvaluationResult>(new PolicyEvaluationResult.Failed(PolicyFailureKind.Unavailable, "not configured"));
    }
}
