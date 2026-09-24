using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Tests.Infrastructure;
using LedgerCore.Ledger.Domain;
using LedgerCore.Ledger.Domain.Journals;
using Npgsql;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>
/// Posting is bound to the journal's own recorded APPROVED decision (Milestone 4, ADR-015): in the
/// application, in the posting transition, in the audit and in the JournalPosted event.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ApprovalEvidenceBindingTests(PostgresFixture db)
{
    private static void AssertGuard(PostgresException error, string code)
    {
        Assert.Equal("LC001", error.SqlState);
        Assert.StartsWith(code + ":", error.MessageText, StringComparison.Ordinal);
    }

    private async Task<long> EvidenceRowsAsync(Guid journal)
    {
        await using var connection = db.CreateRuntimeConnection();
        return await Sql.ScalarAsync<long>(connection, "SELECT count(*) FROM journal_policy_decisions WHERE journal_id = $1", journal);
    }

    /// <summary>
    /// Simulates legacy or tampered data: as the superuser, with triggers disabled for the session,
    /// moves a PENDING_APPROVAL journal to APPROVED without any evidence. Nothing in the product can
    /// do this; the point is that posting still refuses it.
    /// </summary>
    private async Task ForceApprovedWithoutEvidenceAsync(Guid journal)
    {
        await using var superuser = db.CreateSuperuserConnection();
        await superuser.OpenAsync();
        await using var tx = await superuser.BeginTransactionAsync();
        await Sql.ExecuteAsync(superuser, "SET LOCAL session_replication_role = replica");
        await Sql.ExecuteAsync(superuser, "UPDATE journals SET status = 'APPROVED', approved_at = now(), approved_by = 'bypass' WHERE id = $1", journal);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task ValidEvidencePostsAndTheEventNamesIt()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var decision = (await s.GetAsync(id)).PolicyDecision!;

        await using var connection = db.CreateRuntimeConnection();
        Assert.Equal(
            decision.DecisionId.ToString(),
            await Sql.ScalarAsync<string>(connection, "SELECT payload ->> 'policyDecisionId' FROM outbox_events WHERE aggregate_id = $1", id));
        Assert.Equal(
            "APPROVED>" + decision.DecisionId + " POSTED>" + decision.DecisionId,
            await Sql.ScalarAsync<string>(
                connection,
                "SELECT string_agg(to_status || '>' || policy_decision_id, ' ' ORDER BY id) FROM journal_status_transitions WHERE journal_id = $1 AND policy_decision_id IS NOT NULL",
                id));
    }

    [Fact]
    public async Task AJournalApprovedWithoutEvidenceCannotBePosted()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await s.SubmitAsync(id);
        await ForceApprovedWithoutEvidenceAsync(id);

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostAsync(id));

        Assert.Equal("APPROVAL_EVIDENCE_REQUIRED", error.Code);
        Assert.Equal(JournalStatus.Approved, (await s.GetAsync(id)).Journal.Status);
    }

    [Fact]
    public async Task TheDatabaseRefusesThatPostingEvenWithAClaim()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await s.SubmitAsync(id);
        await ForceApprovedWithoutEvidenceAsync(id);
        await using var owner = db.CreateOwnerConnection();
        await owner.OpenAsync();
        await using var tx = await owner.BeginTransactionAsync();
        await Sql.ExecuteAsync(
            owner,
            "INSERT INTO command_idempotency VALUES ($1, 'POST_JOURNAL', 'bypass-key-1', repeat('a', 64), $2, $2, 'sql', NULL, now())",
            s.LedgerId,
            id);

        var error = await Sql.FailsAsync(owner, "UPDATE journals SET status = 'POSTED', posted_at = now(), posted_by = 'sql' WHERE id = $1", id);

        AssertGuard(error, "APPROVAL_EVIDENCE_REQUIRED");
    }

    [Theory]
    [InlineData(nameof(PolicyDecisionValue.Rejected))]
    [InlineData(nameof(PolicyDecisionValue.ReviewRequired))]
    public async Task RejectedOrReviewRequiredJournalsCannotPost(string decision)
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await s.SubmitAsync(id);
        s.Policy.Decide(id, Enum.Parse<PolicyDecisionValue>(decision), "SOME_REASON");
        await s.RequestApprovalAsync(id);

        var error = await Assert.ThrowsAsync<LedgerDomainException>(() => s.PostAsync(id));

        Assert.Equal("JOURNAL_INVALID_STATE", error.Code);
        Assert.NotEqual(JournalStatus.Posted, (await s.GetAsync(id)).Journal.Status);
    }

    [Fact]
    public async Task ADecisionForAnotherTransactionIsNeverRecorded()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await s.SubmitAsync(id);
        var foreign = new PolicyDecision(Guid.NewGuid(), Guid.NewGuid(), "stub-policy@1", PolicyDecisionValue.Approved, [], DateTimeOffset.UtcNow, "1.1.0");
        s.Policy.Respond(id, _ => Task.FromResult<PolicyEvaluationResult>(new PolicyEvaluationResult.Decided(foreign)));

        var outcome = await s.RequestApprovalAsync(id);

        Assert.Equal(PolicyFailureKind.ContractViolation, outcome.Failure);
        Assert.Equal(0, await EvidenceRowsAsync(id));
        Assert.Equal(JournalStatus.PendingApproval, (await s.GetAsync(id)).Journal.Status);
    }

    [Fact]
    public async Task EvidenceOfAnotherJournalCannotBeReused()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var approved = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        var pending = await s.DraftAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await s.SubmitAsync(pending);
        var decisionId = (await s.GetAsync(approved)).PolicyDecision!.DecisionId;
        await using var runtime = db.CreateRuntimeConnection();

        var error = await Sql.FailsAsync(
            runtime,
            "INSERT INTO journal_policy_decisions (journal_id, decision_id, policy_version, decision, reason_codes, evaluated_at, contract_version, received_at) " +
            "VALUES ($1, $2, 'stub-policy@1', 'APPROVED', '{}', now(), '1.1.0', now())",
            pending,
            decisionId);

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("ux_journal_policy_decisions_decision", error.ConstraintName);
    }

    [Fact]
    public async Task EvidenceCannotChangeAfterPosting()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.PostedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await using var runtime = db.CreateRuntimeConnection();
        await using var owner = db.CreateOwnerConnection();

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Sql.FailsAsync(runtime, "UPDATE journal_policy_decisions SET decision = 'REJECTED' WHERE journal_id = $1", id)).SqlState);
        AssertGuard(await Sql.FailsAsync(owner, "UPDATE journal_policy_decisions SET decision_id = gen_random_uuid() WHERE journal_id = $1", id), "POLICY_EVIDENCE_IMMUTABLE");
        AssertGuard(await Sql.FailsAsync(owner, "DELETE FROM journal_policy_decisions WHERE journal_id = $1", id), "POLICY_EVIDENCE_IMMUTABLE");
    }

    [Fact]
    public async Task AJournalPostedEventMustNameThePostingEvidence()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.ApprovedAsync((s.Rent, Debit, 10m), (s.Cash, Credit, 10m));
        await using var owner = db.CreateOwnerConnection();
        await owner.OpenAsync();
        await using var tx = await owner.BeginTransactionAsync();
        await Sql.ExecuteAsync(
            owner,
            "INSERT INTO command_idempotency VALUES ($1, 'POST_JOURNAL', 'sql-post-key-1', repeat('a', 64), $2, $2, 'sql', NULL, now())",
            s.LedgerId,
            id);
        await Sql.ExecuteAsync(owner, "UPDATE journals SET status = 'POSTED', posted_at = now(), posted_by = 'sql' WHERE id = $1", id);
        await Sql.ExecuteAsync(
            owner,
            "INSERT INTO outbox_events (id, aggregate_type, aggregate_id, event_type, payload, created_at) " +
            "VALUES (gen_random_uuid(), 'Journal', $1, 'JournalPosted', jsonb_build_object('policyDecisionId', gen_random_uuid()), now())",
            id);

        var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());

        AssertGuard(error, "OUTBOX_EVIDENCE_MISMATCH");
    }
}
