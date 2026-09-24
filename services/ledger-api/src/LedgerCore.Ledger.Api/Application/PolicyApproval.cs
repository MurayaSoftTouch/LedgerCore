using System.Globalization;
using LedgerCore.Ledger.Api.Integration.Correlation;
using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Persistence;
using LedgerCore.Ledger.Domain;
using LedgerCore.Ledger.Domain.Journals;
using Microsoft.EntityFrameworkCore;

namespace LedgerCore.Ledger.Api.Application;

/// <param name="Journal">The journal after the attempt.</param>
/// <param name="Evidence">The policy decision recorded for it, if any.</param>
/// <param name="Failure">Why no decision was obtained this time; the journal is unchanged.</param>
internal sealed record ApprovalOutcome(Journal Journal, JournalPolicyDecision? Evidence, PolicyFailureKind? Failure);

/// <summary>
/// The only path to APPROVED or REJECTED (ADR-006, ADR-012):
/// <list type="number">
/// <item>Read the PENDING_APPROVAL journal and build the contract request from persisted state.</item>
/// <item>Call the policy service <b>outside</b> any database transaction (with bounded retries).</item>
/// <item>In a new transaction, lock the journal, record the decision as evidence and apply it:
/// APPROVED → APPROVED, REJECTED → REJECTED, REVIEW_REQUIRED → unchanged.</item>
/// </list>
/// Failures (timeout, unavailable, contract violation, …) change nothing. Retrying is safe: the
/// journal id is the policy transactionId, so the policy service returns the same decision, and
/// evidence already recorded is returned without calling it again.
/// </summary>
internal sealed partial class PolicyApproval(
    LedgerDbContext db, IPolicyDecisionClient policy, TimeProvider time, ILogger<PolicyApproval> logger)
{
    /// <summary>Principal recorded as approver/rejecter; the evidence row carries the decision itself.</summary>
    public const string PolicyActor = "policy-service";

    public async Task<ApprovalOutcome> RequestAsync(Guid ledgerId, Guid journalId, string actor, CancellationToken ct)
    {
        var journal = await db.Journals.AsNoTracking().Include(j => j.Entries)
            .SingleOrDefaultAsync(j => j.Id == journalId && j.LedgerId == ledgerId, ct)
            ?? throw NotFound.Journal(journalId);
        var evidence = await db.JournalPolicyDecisions.AsNoTracking().SingleOrDefaultAsync(d => d.JournalId == journalId, ct);

        if (evidence is not null)
        {
            // Already decided (or under manual review): never ask again for the same transaction.
            return new ApprovalOutcome(journal, evidence, null);
        }

        if (journal.Status != JournalStatus.PendingApproval)
        {
            throw new LedgerDomainException(
                DomainErrorKind.InvalidState,
                "JOURNAL_NOT_PENDING_APPROVAL",
                $"Policy approval applies to PENDING_APPROVAL journals; journal is {journal.Status}.");
        }

        var request = await BuildRequestAsync(journal, actor, ct);
        var result = await policy.EvaluateAsync(request, ct);
        if (result is PolicyEvaluationResult.Failed failed)
        {
            return new ApprovalOutcome(journal, null, failed.Kind);
        }

        var decision = ((PolicyEvaluationResult.Decided)result).Decision;
        return await ApplyAsync(ledgerId, journalId, decision, ct);
    }

    private async Task<ApprovalOutcome> ApplyAsync(Guid ledgerId, Guid journalId, PolicyDecision decision, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockJournalAsync(ledgerId, journalId, ct);
        var journal = await db.Journals.Include(j => j.Entries)
            .SingleAsync(j => j.Id == journalId && j.LedgerId == ledgerId, ct);
        var existing = await db.JournalPolicyDecisions.SingleOrDefaultAsync(d => d.JournalId == journalId, ct);

        if (existing is not null)
        {
            // A concurrent request recorded it first. The policy service is idempotent, so it must
            // be the same decision; anything else is a contract violation and changes nothing.
            if (existing.DecisionId != decision.DecisionId)
            {
                LogDecisionMismatch(journalId, existing.DecisionId, decision.DecisionId);
                return new ApprovalOutcome(journal, existing, PolicyFailureKind.ContractViolation);
            }

            return new ApprovalOutcome(journal, existing, null);
        }

        if (journal.Status != JournalStatus.PendingApproval)
        {
            throw new LedgerDomainException(
                DomainErrorKind.InvalidState, "JOURNAL_NOT_PENDING_APPROVAL", $"Journal is {journal.Status}.");
        }

        var now = time.GetUtcNow();
        var evidence = JournalPolicyDecision.From(journalId, decision, CorrelationId.Current, now);
        db.JournalPolicyDecisions.Add(evidence);
        await db.SaveChangesAsync(ct);

        switch (decision.Decision)
        {
            case PolicyDecisionValue.Approved:
                journal.Approve(PolicyActor, now);
                break;
            case PolicyDecisionValue.Rejected:
                journal.Reject(PolicyActor, RejectionReason(decision.ReasonCodes), now);
                break;
            case PolicyDecisionValue.ReviewRequired:
                // Manual review remains necessary; the journal stays PENDING_APPROVAL.
                break;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        LogDecisionRecorded(journalId, decision.TransactionId, decision.DecisionId, decision.PolicyVersion, decision.Decision);
        return new ApprovalOutcome(journal, evidence, null);
    }

    private async Task<PolicyDecisionRequest> BuildRequestAsync(Journal journal, string actor, CancellationToken ct)
    {
        var accountIds = journal.Entries.Select(e => e.AccountId).Distinct().ToArray();
        var accountTypes = await db.Accounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Type, ct);
        var totals = DoubleEntry.Totals(journal.Entries);

        return new PolicyDecisionRequest(
            TransactionId: journal.Id,
            // The ledger has no organization model yet: a ledger is the policy scope (documented in ADR-012).
            OrganizationId: journal.LedgerId,
            TransactionType: EnumText.ToText(journal.Type),
            Currency: journal.Currency.Code,
            TotalAmount: totals.Debits.ToString("F" + journal.Currency.MinorUnits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            AccountContext: [.. journal.Entries
                .OrderBy(e => e.LineNumber)
                .Select(e => new PolicyAccountContext(e.AccountId, EnumText.ToText(accountTypes[e.AccountId]), EnumText.ToText(e.Direction)))
                .Distinct()],
            RequestedBy: actor,
            RequestedAt: time.GetUtcNow());
    }

    /// <summary>The journal keeps a bounded summary; the full list is in the evidence row.</summary>
    private static string RejectionReason(IReadOnlyList<string> reasonCodes)
    {
        var reason = "POLICY_REJECTED";
        foreach (var code in reasonCodes)
        {
            if (reason.Length + code.Length + 1 > 500)
            {
                break;
            }

            reason += "," + code;
        }

        return reason;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Policy decision recorded for journal {JournalId}: transaction {PolicyTransactionId}, decision {PolicyDecisionId} ({PolicyVersion}) {Decision}")]
    private partial void LogDecisionRecorded(Guid journalId, Guid policyTransactionId, Guid policyDecisionId, string policyVersion, PolicyDecisionValue decision);

    [LoggerMessage(Level = LogLevel.Error, Message = "Policy returned decision {ReceivedDecisionId} for journal {JournalId}, which already has decision {RecordedDecisionId}; ignored")]
    private partial void LogDecisionMismatch(Guid journalId, Guid recordedDecisionId, Guid receivedDecisionId);
}
