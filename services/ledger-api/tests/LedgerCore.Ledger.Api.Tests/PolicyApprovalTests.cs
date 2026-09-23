using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Tests.Infrastructure;
using LedgerCore.Ledger.Domain;
using LedgerCore.Ledger.Domain.Journals;
using Npgsql;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>The ledger side of the approval flow against real PostgreSQL, with a stubbed policy service.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class PolicyApprovalTests(PostgresFixture db)
{
    private async Task<(LedgerScenario S, Guid Journal)> PendingAsync(decimal amount = 1500.25m)
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync((s.Rent, Debit, amount), (s.Cash, Credit, amount));
        await s.SubmitAsync(id);
        return (s, id);
    }

    private async Task<long> CountAsync(string sql, Guid journal)
    {
        await using var connection = db.CreateRuntimeConnection();
        return await Sql.ScalarAsync<long>(connection, sql, journal);
    }

    private Task<long> EvidenceRowsAsync(Guid journal) =>
        CountAsync("SELECT count(*) FROM journal_policy_decisions WHERE journal_id = $1", journal);

    private Task<long> TransitionsToAsync(Guid journal, string status) =>
        CountAsync($"SELECT count(*) FROM journal_status_transitions WHERE journal_id = $1 AND to_status = '{status}'", journal);

    [Fact]
    public async Task ApprovedDecisionApprovesAndIsRecordedAsEvidence()
    {
        var (s, id) = await PendingAsync();
        s.Policy.Decide(id, PolicyDecisionValue.Approved);

        var outcome = await s.RequestApprovalAsync(id);

        Assert.Null(outcome.Failure);
        Assert.Equal(JournalStatus.Approved, outcome.Journal.Status);
        Assert.Equal("policy-service", outcome.Journal.ApprovedBy);
        var view = await s.GetAsync(id);
        Assert.Equal(PolicyDecisionValue.Approved, view.PolicyDecision!.Decision);
        Assert.Equal("stub-policy@1", view.PolicyDecision.PolicyVersion);
        Assert.Equal(1, await EvidenceRowsAsync(id));

        // Posting is now possible.
        Assert.Equal(JournalStatus.Posted, (await s.PostAsync(id)).Status);
    }

    [Fact]
    public async Task RequestIsBuiltFromPersistedStateOnly()
    {
        var (s, id) = await PendingAsync(1500.25m);
        s.Policy.Decide(id, PolicyDecisionValue.Approved);

        await s.RequestApprovalAsync(id);

        var request = Assert.Single(s.Policy.Requests);
        Assert.Equal(id, request.TransactionId);
        Assert.Equal(s.LedgerId, request.OrganizationId);
        Assert.Equal("PAYMENT", request.TransactionType);
        Assert.Equal("KES", request.Currency);
        Assert.Equal("1500.25", request.TotalAmount);
        Assert.Equal(
            [new PolicyAccountContext(s.Rent, "EXPENSE", "DEBIT"), new PolicyAccountContext(s.Cash, "ASSET", "CREDIT")],
            request.AccountContext);
        Assert.Equal("approval-requester", request.RequestedBy);
    }

    [Fact]
    public async Task RejectedDecisionRejectsAndPostingIsImpossible()
    {
        var (s, id) = await PendingAsync();
        s.Policy.Decide(id, PolicyDecisionValue.Rejected, "AMOUNT_EXCEEDS_HARD_LIMIT", "ACCOUNT_CONTEXT_RESTRICTED");

        var outcome = await s.RequestApprovalAsync(id);

        Assert.Equal(JournalStatus.Rejected, outcome.Journal.Status);
        Assert.Equal("POLICY_REJECTED,AMOUNT_EXCEEDS_HARD_LIMIT,ACCOUNT_CONTEXT_RESTRICTED", outcome.Journal.RejectionReason);
        Assert.Equal(["AMOUNT_EXCEEDS_HARD_LIMIT", "ACCOUNT_CONTEXT_RESTRICTED"], outcome.Evidence!.ReasonCodes);
        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostAsync(id));
        Assert.Equal("JOURNAL_INVALID_STATE", error.Code);
    }

    [Fact]
    public async Task ReviewRequiredKeepsTheJournalPendingAndIsNotRequestedAgain()
    {
        var (s, id) = await PendingAsync();
        s.Policy.Decide(id, PolicyDecisionValue.ReviewRequired, "AMOUNT_EXCEEDS_REVIEW_THRESHOLD");

        var first = await s.RequestApprovalAsync(id);
        var second = await s.RequestApprovalAsync(id);

        Assert.Equal(JournalStatus.PendingApproval, first.Journal.Status);
        Assert.Equal(PolicyDecisionValue.ReviewRequired, first.Evidence!.Decision);
        Assert.Equal(first.Evidence.DecisionId, second.Evidence!.DecisionId);
        Assert.Equal(1, s.Policy.CallsFor(id));
        await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostAsync(id));
    }

    public static TheoryData<string> FailureKinds() => [.. Enum.GetNames<PolicyFailureKind>()];

    [Theory]
    [MemberData(nameof(FailureKinds))]
    public async Task EveryFailureLeavesTheJournalPendingWithoutEvidenceAndIsRecoverable(string kind)
    {
        var (s, id) = await PendingAsync();
        s.Policy.Fail(id, Enum.Parse<PolicyFailureKind>(kind));

        var failed = await s.RequestApprovalAsync(id);

        Assert.Equal(Enum.Parse<PolicyFailureKind>(kind), failed.Failure);
        Assert.Equal(JournalStatus.PendingApproval, failed.Journal.Status);
        Assert.Null(failed.Evidence);
        Assert.Equal(0, await EvidenceRowsAsync(id));
        await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostAsync(id));

        // Never a fake rejection: once the policy service answers, the journal is decided normally.
        s.Policy.Decide(id, PolicyDecisionValue.Approved);
        Assert.Equal(JournalStatus.Approved, (await s.RequestApprovalAsync(id)).Journal.Status);
    }

    [Fact]
    public async Task AmbiguousOutcomeIsRecoveredByRetryingWithTheSameTransaction()
    {
        // The policy service commits a decision but the response is lost; the retry returns it.
        var (s, id) = await PendingAsync();
        var committed = new PolicyDecision(
            Guid.NewGuid(), id, "stub-policy@1", PolicyDecisionValue.Approved, [], DateTimeOffset.UtcNow, "1.1.0");
        var calls = 0;
        s.Policy.Respond(id, _ => Task.FromResult<PolicyEvaluationResult>(Interlocked.Increment(ref calls) == 1
            ? new PolicyEvaluationResult.Failed(PolicyFailureKind.Unavailable, "response lost")
            : new PolicyEvaluationResult.Decided(committed)));

        var lost = await s.RequestApprovalAsync(id);
        var retried = await s.RequestApprovalAsync(id);

        Assert.Equal(JournalStatus.PendingApproval, lost.Journal.Status);
        Assert.Equal(JournalStatus.Approved, retried.Journal.Status);
        Assert.Equal(committed.DecisionId, retried.Evidence!.DecisionId);
        Assert.Equal(1, await EvidenceRowsAsync(id));
        Assert.Equal(1, await TransitionsToAsync(id, "APPROVED"));
    }

    [Fact]
    public async Task ConcurrentApprovalRequestsRecordOneDecisionAndOneTransition()
    {
        const int callers = 8;
        var (s, id) = await PendingAsync();
        var decision = new PolicyDecision(Guid.NewGuid(), id, "stub-policy@1", PolicyDecisionValue.Approved, [], DateTimeOffset.UtcNow, "1.1.0");
        var allCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        s.Policy.Respond(id, async _ =>
        {
            // Every caller reaches the policy service before any of them records the decision.
            if (Interlocked.Increment(ref arrived) == callers)
            {
                allCalled.SetResult();
            }

            await allCalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return new PolicyEvaluationResult.Decided(decision);
        });

        var outcomes = await Task.WhenAll(Enumerable.Range(0, callers).Select(_ => s.RequestApprovalAsync(id)));

        Assert.All(outcomes, o => Assert.Null(o.Failure));
        Assert.All(outcomes, o => Assert.Equal(decision.DecisionId, o.Evidence!.DecisionId));
        Assert.All(outcomes, o => Assert.Equal(JournalStatus.Approved, o.Journal.Status));
        Assert.Equal(1, await EvidenceRowsAsync(id));
        Assert.Equal(1, await TransitionsToAsync(id, "APPROVED"));
    }

    [Fact]
    public async Task ConflictingDecisionsFromAMisbehavingPolicyServiceNeverOverwriteTheFirst()
    {
        var (s, id) = await PendingAsync();
        var bothCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        s.Policy.Respond(id, async _ =>
        {
            var n = Interlocked.Increment(ref arrived);
            if (n == 2)
            {
                bothCalled.SetResult();
            }

            await bothCalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return new PolicyEvaluationResult.Decided(new PolicyDecision(
                Guid.NewGuid(), id, "stub-policy@1", n == 1 ? PolicyDecisionValue.Approved : PolicyDecisionValue.Rejected,
                n == 1 ? [] : ["X_Y"], DateTimeOffset.UtcNow, "1.1.0"));
        });

        var outcomes = await Task.WhenAll(s.RequestApprovalAsync(id), s.RequestApprovalAsync(id));

        Assert.Single(outcomes, o => o.Failure is null);
        Assert.Single(outcomes, o => o.Failure == PolicyFailureKind.ContractViolation);
        Assert.Equal(1, await EvidenceRowsAsync(id));
        var winner = outcomes.Single(o => o.Failure is null).Evidence!;
        Assert.Equal(winner.DecisionId, (await s.GetAsync(id)).PolicyDecision!.DecisionId);
    }

    [Fact]
    public async Task PostingCannotOvertakeAnInFlightApproval()
    {
        var (s, id) = await PendingAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new PolicyDecision(Guid.NewGuid(), id, "stub-policy@1", PolicyDecisionValue.Approved, [], DateTimeOffset.UtcNow, "1.1.0");
        s.Policy.Respond(id, async _ =>
        {
            reached.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return new PolicyEvaluationResult.Decided(decision);
        });

        var approval = s.RequestApprovalAsync(id);
        await reached.Task;
        var early = await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostAsync(id));
        release.SetResult();
        await approval;

        Assert.Equal("JOURNAL_INVALID_STATE", early.Code);
        Assert.Equal(JournalStatus.Posted, (await s.PostAsync(id)).Status);
    }

    [Fact]
    public async Task DraftCannotRequestApproval()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync((s.Rent, Debit, 1m), (s.Cash, Credit, 1m));

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.RequestApprovalAsync(id));

        Assert.Equal("JOURNAL_NOT_PENDING_APPROVAL", error.Code);
        Assert.Equal(0, s.Policy.CallsFor(id));
    }

    [Fact]
    public async Task ReversalIsSentAsReversalType()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var original = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var reversal = await s.ReverseAsync(original);
        s.Policy.Decide(reversal.Id, PolicyDecisionValue.Approved);

        await s.RequestApprovalAsync(reversal.Id);

        Assert.Equal("REVERSAL", s.Policy.Requests.Single(r => r.TransactionId == reversal.Id).TransactionType);
    }

    private static void AssertGuard(PostgresException error, string code)
    {
        Assert.Equal("LC001", error.SqlState);
        Assert.StartsWith(code + ":", error.MessageText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UPDATE journals SET status = 'APPROVED', approved_at = now(), approved_by = 'sql' WHERE id = $1", "APPROVAL_EVIDENCE_REQUIRED")]
    [InlineData("UPDATE journals SET status = 'REJECTED', rejected_at = now(), rejected_by = 'sql', rejection_reason = 'x' WHERE id = $1", "REJECTION_EVIDENCE_REQUIRED")]
    public async Task DatabaseRefusesApprovalOrRejectionWithoutMatchingEvidence(string sql, string code)
    {
        var (_, id) = await PendingAsync();
        await using var runtime = db.CreateRuntimeConnection();
        await using var owner = db.CreateOwnerConnection();

        AssertGuard(await Sql.FailsAsync(runtime, sql, id), code);
        AssertGuard(await Sql.FailsAsync(owner, sql, id), code);
    }

    [Fact]
    public async Task ReviewEvidenceDoesNotAllowApprovalBySql()
    {
        var (s, id) = await PendingAsync();
        s.Policy.Decide(id, PolicyDecisionValue.ReviewRequired, "MANUAL");
        await s.RequestApprovalAsync(id);
        await using var runtime = db.CreateRuntimeConnection();

        AssertGuard(
            await Sql.FailsAsync(runtime, "UPDATE journals SET status = 'APPROVED', approved_at = now(), approved_by = 'sql' WHERE id = $1", id),
            "APPROVAL_EVIDENCE_REQUIRED");
    }

    [Fact]
    public async Task EvidenceIsAppendOnly()
    {
        var (s, id) = await PendingAsync();
        await s.ApproveAsync(id);
        await using var runtime = db.CreateRuntimeConnection();
        await using var owner = db.CreateOwnerConnection();

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Sql.FailsAsync(runtime, "UPDATE journal_policy_decisions SET decision = 'REJECTED' WHERE journal_id = $1", id)).SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Sql.FailsAsync(runtime, "DELETE FROM journal_policy_decisions WHERE journal_id = $1", id)).SqlState);
        AssertGuard(await Sql.FailsAsync(owner, "UPDATE journal_policy_decisions SET policy_version = 'forged@9' WHERE journal_id = $1", id), "POLICY_EVIDENCE_IMMUTABLE");
        AssertGuard(await Sql.FailsAsync(owner, "DELETE FROM journal_policy_decisions WHERE journal_id = $1", id), "POLICY_EVIDENCE_IMMUTABLE");
        AssertGuard(await Sql.FailsAsync(owner, "TRUNCATE journal_policy_decisions"), "LEDGER_HISTORY_IMMUTABLE");
    }

    [Fact]
    public async Task EvidenceCanOnlyBeRecordedForPendingJournals()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var draft = await s.DraftAsync((s.Rent, Debit, 1m), (s.Cash, Credit, 1m));
        await using var runtime = db.CreateRuntimeConnection();

        var error = await Sql.FailsAsync(
            runtime,
            "INSERT INTO journal_policy_decisions (journal_id, decision_id, policy_version, decision, reason_codes, evaluated_at, contract_version, received_at) " +
            "VALUES ($1, gen_random_uuid(), 'forged@1', 'APPROVED', '{}', now(), '1.1.0', now())",
            draft);

        AssertGuard(error, "JOURNAL_NOT_PENDING_APPROVAL");
    }

    [Fact]
    public async Task TransactionTypeIsImmutable()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var draft = await s.DraftAsync((s.Rent, Debit, 1m), (s.Cash, Credit, 1m));
        await using var runtime = db.CreateRuntimeConnection();

        AssertGuard(await Sql.FailsAsync(runtime, "UPDATE journals SET transaction_type = 'FEE' WHERE id = $1", draft), "JOURNAL_IMMUTABLE");
    }
}
