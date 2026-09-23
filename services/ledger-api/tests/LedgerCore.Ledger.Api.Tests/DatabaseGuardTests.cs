using LedgerCore.Ledger.Api.Tests.Infrastructure;
using LedgerCore.Ledger.Domain.Journals;
using Npgsql;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>
/// Raw SQL that bypasses the application. Each test states which layer stops it:
/// a missing privilege for the runtime role (42501), or a trigger/constraint that also stops the
/// schema owner (LC001 / 23xxx).
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DatabaseGuardTests(PostgresFixture db)
{
    private const string InsufficientPrivilege = PostgresErrorCodes.InsufficientPrivilege;
    private const string LedgerGuard = "LC001";

    [Fact]
    public async Task PostedEntryCannotBeUpdated()
    {
        var (s, id) = await PostedJournalAsync();
        await using var runtime = db.CreateRuntimeConnection();
        await using var owner = db.CreateOwnerConnection();

        var asRuntime = await Sql.FailsAsync(runtime, "UPDATE journal_entries SET amount = amount + 1 WHERE journal_id = $1", id);
        var asOwner = await Sql.FailsAsync(owner, "UPDATE journal_entries SET amount = amount + 1 WHERE journal_id = $1", id);

        Assert.Equal(InsufficientPrivilege, asRuntime.SqlState);
        AssertGuard(asOwner, "JOURNAL_ENTRY_IMMUTABLE");
        await AssertEntriesUnchangedAsync(s, id);
    }

    [Fact]
    public async Task PostedEntryCannotBeDeleted()
    {
        var (s, id) = await PostedJournalAsync();
        await using var runtime = db.CreateRuntimeConnection();
        await using var owner = db.CreateOwnerConnection();

        Assert.Equal(InsufficientPrivilege, (await Sql.FailsAsync(runtime, "DELETE FROM journal_entries WHERE journal_id = $1", id)).SqlState);
        AssertGuard(await Sql.FailsAsync(owner, "DELETE FROM journal_entries WHERE journal_id = $1", id), "JOURNAL_ENTRY_IMMUTABLE");
        await AssertEntriesUnchangedAsync(s, id);
    }

    [Fact]
    public async Task PostedJournalCannotBeDeleted()
    {
        var (_, id) = await PostedJournalAsync();
        await using var runtime = db.CreateRuntimeConnection();
        await using var owner = db.CreateOwnerConnection();

        Assert.Equal(InsufficientPrivilege, (await Sql.FailsAsync(runtime, "DELETE FROM journals WHERE id = $1", id)).SqlState);
        AssertGuard(await Sql.FailsAsync(owner, "DELETE FROM journals WHERE id = $1", id), "JOURNAL_IMMUTABLE");
    }

    [Theory]
    [InlineData("UPDATE journals SET description = 'rewritten' WHERE id = $1")]
    [InlineData("UPDATE journals SET currency = 'USD' WHERE id = $1")]
    [InlineData("UPDATE journals SET posted_at = now() - interval '1 day' WHERE id = $1")]
    [InlineData("UPDATE journals SET status = 'APPROVED', posted_at = NULL, posted_by = NULL WHERE id = $1")]
    [InlineData("UPDATE journals SET external_reference = 'forged' WHERE id = $1")]
    public async Task PostedJournalCannotBeModifiedEvenByOwner(string sql)
    {
        var (_, id) = await PostedJournalAsync();
        await using var runtime = db.CreateRuntimeConnection();
        await using var owner = db.CreateOwnerConnection();

        AssertGuard(await Sql.FailsAsync(runtime, sql, id), "JOURNAL_IMMUTABLE");
        AssertGuard(await Sql.FailsAsync(owner, sql, id), "JOURNAL_IMMUTABLE");
    }

    [Fact]
    public async Task EntriesCannotBeAddedToPostedJournal()
    {
        var (s, id) = await PostedJournalAsync();
        await using var runtime = db.CreateRuntimeConnection();

        var error = await Sql.FailsAsync(
            runtime,
            "INSERT INTO journal_entries (id, journal_id, ledger_id, currency, line_number, account_id, direction, amount) " +
            "VALUES (gen_random_uuid(), $1, $2, 'KES', 99, $3, 'DEBIT', 1)",
            id, s.LedgerId, s.Rent);

        AssertGuard(error, "JOURNAL_ENTRY_IMMUTABLE");
    }

    [Fact]
    public async Task UnbalancedJournalCannotBeSubmittedBySql()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync((s.Rent, Debit, 100m), (s.Cash, Credit, 99.99m));
        await using var runtime = db.CreateRuntimeConnection();

        var error = await Sql.FailsAsync(
            runtime, "UPDATE journals SET status = 'PENDING_APPROVAL', submitted_at = now(), submitted_by = 'sql' WHERE id = $1", id);

        AssertGuard(error, "JOURNAL_UNBALANCED");
    }

    [Fact]
    public async Task OneSidedJournalCannotBeSubmittedBySql()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync((s.Rent, Debit, 100m), (s.Cash, Debit, 100m));
        await using var runtime = db.CreateRuntimeConnection();

        var error = await Sql.FailsAsync(
            runtime, "UPDATE journals SET status = 'PENDING_APPROVAL', submitted_at = now(), submitted_by = 'sql' WHERE id = $1", id);

        AssertGuard(error, "JOURNAL_UNBALANCED");
    }

    [Theory]
    [InlineData("UPDATE journals SET status = 'POSTED', submitted_at = now(), submitted_by = 'x', approved_at = now(), approved_by = 'x', posted_at = now(), posted_by = 'x' WHERE id = $1")]
    [InlineData("UPDATE journals SET status = 'APPROVED', submitted_at = now(), submitted_by = 'x', approved_at = now(), approved_by = 'x' WHERE id = $1")]
    public async Task LifecycleStepsCannotBeSkippedBySql(string sql)
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync((s.Rent, Debit, 1m), (s.Cash, Credit, 1m));
        await using var runtime = db.CreateRuntimeConnection();

        AssertGuard(await Sql.FailsAsync(runtime, sql, id), "JOURNAL_INVALID_TRANSITION");
    }

    [Fact]
    public async Task JournalsCannotBeInsertedPastDraft()
    {
        var s = await LedgerScenario.CreateAsync(db);
        await using var runtime = db.CreateRuntimeConnection();

        var error = await Sql.FailsAsync(
            runtime,
            "INSERT INTO journals (id, ledger_id, currency, transaction_type, description, status, created_at, created_by, submitted_at, submitted_by, approved_at, approved_by, posted_at, posted_by) " +
            "VALUES (gen_random_uuid(), $1, 'KES', 'PAYMENT', 'forged', 'POSTED', now(), 'x', now(), 'x', now(), 'x', now(), 'x')",
            s.LedgerId);

        AssertGuard(error, "JOURNAL_INVALID_STATE");
    }

    [Fact]
    public async Task ZeroAndNegativeAmountsAreRejectedByCheckConstraint()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync();
        await using var runtime = db.CreateRuntimeConnection();

        foreach (var amount in new[] { 0m, -5m })
        {
            var error = await Sql.FailsAsync(
                runtime,
                "INSERT INTO journal_entries (id, journal_id, ledger_id, currency, line_number, account_id, direction, amount) " +
                "VALUES (gen_random_uuid(), $1, $2, 'KES', 1, $3, 'DEBIT', $4)",
                id, s.LedgerId, s.Rent, amount);
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("ck_journal_entries_amount_positive", error.ConstraintName);
        }
    }

    [Fact]
    public async Task EntryCannotUseAccountFromAnotherLedger()
    {
        var mine = await LedgerScenario.CreateAsync(db);
        var other = await LedgerScenario.CreateAsync(db);
        var id = await mine.DraftAsync();
        await using var runtime = db.CreateRuntimeConnection();

        var error = await Sql.FailsAsync(
            runtime,
            "INSERT INTO journal_entries (id, journal_id, ledger_id, currency, line_number, account_id, direction, amount) " +
            "VALUES (gen_random_uuid(), $1, $2, 'KES', 1, $3, 'DEBIT', 1)",
            id, mine.LedgerId, other.Rent);

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        Assert.Equal("fk_journal_entries_account", error.ConstraintName);
    }

    [Fact]
    public async Task EntryCurrencyMustMatchJournalAndAccount()
    {
        var s = await LedgerScenario.CreateAsync(db);
        var id = await s.DraftAsync();
        await using var runtime = db.CreateRuntimeConnection();

        var error = await Sql.FailsAsync(
            runtime,
            "INSERT INTO journal_entries (id, journal_id, ledger_id, currency, line_number, account_id, direction, amount) " +
            "VALUES (gen_random_uuid(), $1, $2, 'USD', 1, $3, 'DEBIT', 1)",
            id, s.LedgerId, s.Rent);

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task AccountsCannotBeDeletedOrRetyped()
    {
        var (s, _) = await PostedJournalAsync();
        await using var runtime = db.CreateRuntimeConnection();
        await using var owner = db.CreateOwnerConnection();

        Assert.Equal(InsufficientPrivilege, (await Sql.FailsAsync(runtime, "DELETE FROM accounts WHERE id = $1", s.Rent)).SqlState);
        AssertGuard(await Sql.FailsAsync(owner, "DELETE FROM accounts WHERE id = $1", s.Rent), "ACCOUNT_DELETE_FORBIDDEN");
        AssertGuard(await Sql.FailsAsync(runtime, "UPDATE accounts SET type = 'ASSET' WHERE id = $1", s.Rent), "ACCOUNT_IMMUTABLE");
        AssertGuard(await Sql.FailsAsync(runtime, "UPDATE accounts SET currency = 'USD' WHERE id = $1", s.Rent), "ACCOUNT_IMMUTABLE");
    }

    [Fact]
    public async Task InactiveAccountCannotBeReactivatedBySql()
    {
        var s = await LedgerScenario.CreateAsync(db);
        await s.DeactivateAsync(s.Rent);
        await using var runtime = db.CreateRuntimeConnection();

        AssertGuard(
            await Sql.FailsAsync(runtime, "UPDATE accounts SET is_active = true, deactivated_at = NULL WHERE id = $1", s.Rent),
            "ACCOUNT_IMMUTABLE");
    }

    [Fact]
    public async Task AuditTrailIsAppendOnlyAndNotWritableByRuntime()
    {
        var (_, id) = await PostedJournalAsync();
        await using var runtime = db.CreateRuntimeConnection();
        await using var owner = db.CreateOwnerConnection();

        Assert.Equal(
            InsufficientPrivilege,
            (await Sql.FailsAsync(runtime, "INSERT INTO journal_status_transitions (journal_id, to_status, actor, occurred_at) VALUES ($1, 'POSTED', 'forger', now())", id)).SqlState);
        Assert.Equal(InsufficientPrivilege, (await Sql.FailsAsync(runtime, "DELETE FROM journal_status_transitions WHERE journal_id = $1", id)).SqlState);
        AssertGuard(await Sql.FailsAsync(owner, "UPDATE journal_status_transitions SET actor = 'forger' WHERE journal_id = $1", id), "AUDIT_IMMUTABLE");
        AssertGuard(await Sql.FailsAsync(owner, "DELETE FROM journal_status_transitions WHERE journal_id = $1", id), "AUDIT_IMMUTABLE");
    }

    [Theory]
    [InlineData("journals")]
    [InlineData("journal_entries")]
    [InlineData("accounts")]
    [InlineData("journal_status_transitions")]
    public async Task TruncateIsForbiddenEvenForOwner(string table)
    {
        await using var owner = db.CreateOwnerConnection();

        AssertGuard(await Sql.FailsAsync(owner, $"TRUNCATE {table} CASCADE"), "LEDGER_HISTORY_IMMUTABLE");
    }

    [Theory]
    [InlineData("ALTER TABLE journals DISABLE TRIGGER journals_before_update")]
    [InlineData("DROP TRIGGER journal_entries_guard ON journal_entries")]
    [InlineData("CREATE TABLE shadow_ledger (id int)")]
    public async Task RuntimeRoleCannotChangeSchemaOrDisableGuards(string ddl)
    {
        await using var runtime = db.CreateRuntimeConnection();

        var error = await Sql.FailsAsync(runtime, ddl);

        Assert.Equal(InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task ReversalWithTamperedEntriesIsRejectedByMirrorCheck()
    {
        var (s, original) = await PostedJournalAsync();
        await using var runtime = db.CreateRuntimeConnection();
        await using var tx = await OpenTransactionAsync(runtime);
        var reversal = Guid.CreateVersion7();

        await Sql.ExecuteAsync(
            runtime,
            "INSERT INTO journals (id, ledger_id, currency, transaction_type, description, status, reverses_journal_id, created_at, created_by) " +
            "VALUES ($1, $2, 'KES', 'REVERSAL', 'tampered', 'DRAFT', $3, now(), 'sql')",
            reversal, s.LedgerId, original);
        // Balanced, but not the mirror of the original (which was Rent Dr 250 / Cash Cr 250).
        foreach (var (line, account, direction) in new[] { (1, s.Cash, "DEBIT"), (2, s.Revenue, "CREDIT") })
        {
            await Sql.ExecuteAsync(
                runtime,
                "INSERT INTO journal_entries (id, journal_id, ledger_id, currency, line_number, account_id, direction, amount) " +
                "VALUES (gen_random_uuid(), $1, $2, 'KES', $3, $4, $5, 250)",
                reversal, s.LedgerId, line, account, direction);
        }

        var error = await Sql.FailsAsync(
            runtime, "UPDATE journals SET status = 'PENDING_APPROVAL', submitted_at = now(), submitted_by = 'sql' WHERE id = $1", reversal);

        AssertGuard(error, "REVERSAL_MISMATCH");
    }

    [Fact]
    public async Task ReversalCannotBeCommittedInDraft()
    {
        var (s, original) = await PostedJournalAsync();
        await using var runtime = db.CreateRuntimeConnection();
        await using var tx = await OpenTransactionAsync(runtime);

        await Sql.ExecuteAsync(
            runtime,
            "INSERT INTO journals (id, ledger_id, currency, transaction_type, description, status, reverses_journal_id, created_at, created_by) " +
            "VALUES (gen_random_uuid(), $1, 'KES', 'REVERSAL', 'stuck', 'DRAFT', $2, now(), 'sql')",
            s.LedgerId, original);

        var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        AssertGuard(error, "REVERSAL_LEFT_IN_DRAFT");
    }

    [Fact]
    public async Task PostedHistoryStaysReadableAfterAccountDeactivation()
    {
        var (s, id) = await PostedJournalAsync();
        await s.DeactivateAsync(s.Rent);
        await s.DeactivateAsync(s.Cash);

        var view = await s.GetAsync(id);

        Assert.Equal(JournalStatus.Posted, view.Journal.Status);
        Assert.Equal([s.Rent, s.Cash], view.Journal.Entries.OrderBy(e => e.LineNumber).Select(e => e.AccountId));
        Assert.All(view.Journal.Entries, e => Assert.Equal(250m, e.Amount));
    }

    private static void AssertGuard(PostgresException error, string code)
    {
        Assert.Equal(LedgerGuard, error.SqlState);
        Assert.StartsWith(code + ":", error.MessageText, StringComparison.Ordinal);
    }

    private static async Task<NpgsqlTransaction> OpenTransactionAsync(NpgsqlConnection connection)
    {
        await connection.OpenAsync();
        return await connection.BeginTransactionAsync();
    }

    private async Task<(LedgerScenario Scenario, Guid JournalId)> PostedJournalAsync()
    {
        var s = await LedgerScenario.CreateAsync(db);
        return (s, await s.PostedAsync((s.Rent, Debit, 250m), (s.Cash, Credit, 250m)));
    }

    private static async Task AssertEntriesUnchangedAsync(LedgerScenario s, Guid id)
    {
        var entries = (await s.GetAsync(id)).Journal.Entries;
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(250m, e.Amount));
    }
}
