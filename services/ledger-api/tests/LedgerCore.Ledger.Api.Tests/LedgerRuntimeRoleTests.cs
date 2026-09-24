using LedgerCore.Ledger.Api.Tests.Infrastructure;
using Npgsql;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>
/// Destructive probes as <c>ledger_runtime</c> (Milestone 5, ADR-007): each statement runs in its own
/// transaction and must fail, with a privilege error or a ledger guard. Nothing is committed.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class LedgerRuntimeRoleTests(PostgresFixture db)
{
    [Theory]
    [InlineData("DROP TABLE journals CASCADE")]
    [InlineData("DROP TABLE journal_status_transitions")]
    [InlineData("ALTER TABLE journals ADD COLUMN probe int")]
    [InlineData("ALTER TABLE journal_entries ALTER COLUMN amount TYPE float8")]
    [InlineData("CREATE TABLE probe (id int)")]
    [InlineData("CREATE OR REPLACE FUNCTION ledger_violation(code text, detail text) RETURNS void LANGUAGE sql AS 'SELECT'")]
    [InlineData("DROP TRIGGER journals_before_update ON journals")]
    [InlineData("ALTER TABLE journals DISABLE TRIGGER journals_before_update")]
    [InlineData("ALTER TABLE journals DISABLE TRIGGER ALL")]
    [InlineData("SET session_replication_role = replica")]
    [InlineData("TRUNCATE journals CASCADE")]
    [InlineData("TRUNCATE journal_status_transitions")]
    [InlineData("TRUNCATE outbox_events")]
    [InlineData("TRUNCATE command_idempotency")]
    [InlineData("TRUNCATE journal_policy_decisions")]
    [InlineData("DELETE FROM journals")]
    [InlineData("DELETE FROM journal_entries")]
    [InlineData("UPDATE journal_entries SET amount = amount")]
    [InlineData("DELETE FROM journal_status_transitions")]
    [InlineData("UPDATE journal_status_transitions SET actor = actor")]
    [InlineData("INSERT INTO journal_status_transitions (journal_id, to_status, actor, occurred_at) SELECT id, 'POSTED', 'x', now() FROM journals LIMIT 1")]
    [InlineData("DELETE FROM journal_policy_decisions")]
    [InlineData("UPDATE journal_policy_decisions SET decision = decision")]
    [InlineData("DELETE FROM outbox_events")]
    [InlineData("UPDATE outbox_events SET payload = payload")]
    [InlineData("DELETE FROM command_idempotency")]
    [InlineData("UPDATE command_idempotency SET requested_by = requested_by")]
    [InlineData("DELETE FROM __ef_migrations_history")]
    [InlineData("INSERT INTO __ef_migrations_history VALUES ('99990101000000_Forged', '10.0')")]
    [InlineData("ALTER ROLE ledger_runtime SUPERUSER")]
    [InlineData("CREATE ROLE probe_role")]
    [InlineData("GRANT ledger_app TO ledger_runtime")]
    public async Task TheRuntimeRoleCannotDoIt(string sql)
    {
        await using var runtime = db.CreateRuntimeConnection();
        await runtime.OpenAsync();
        await using var tx = await runtime.BeginTransactionAsync();

        var error = await Sql.FailsAsync(runtime, sql);

        Assert.True(
            error.SqlState is PostgresErrorCodes.InsufficientPrivilege or "LC001",
            $"{sql}: {error.SqlState} {error.MessageText}");
    }

    [Fact]
    public async Task TheRuntimeRoleIsNotTheOwnerOrASuperuser()
    {
        await using var runtime = db.CreateRuntimeConnection();

        Assert.Equal(
            "ledger_runtime|false|false|false|ledger_app",
            await Sql.ScalarAsync<string>(
                runtime,
                "SELECT current_user || '|' || r.rolsuper || '|' || r.rolcreaterole || '|' || r.rolcreatedb || '|' || " +
                "(SELECT tableowner FROM pg_tables WHERE tablename = 'journals') FROM pg_roles r WHERE r.rolname = current_user"));
    }
}
