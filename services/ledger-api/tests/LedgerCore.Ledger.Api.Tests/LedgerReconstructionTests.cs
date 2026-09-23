using LedgerCore.Ledger.Api.Tests.Infrastructure;
using Npgsql;
using static LedgerCore.Ledger.Domain.Journals.EntryDirection;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>
/// Correctness proof for the domain model (not the reconciliation subsystem): balances are rebuilt
/// with plain SQL from POSTED journal entries only, independently of the application code.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class LedgerReconstructionTests(PostgresFixture db)
{
    [Fact]
    public async Task BalancesRebuiltFromPostedEntriesAreConsistent()
    {
        var s = await LedgerScenario.CreateAsync(db);

        // Posted activity.
        await s.PostedAsync((s.Bank, Debit, 100_000m), (s.Revenue, Credit, 100_000m));             // sale
        await s.PostedAsync((s.Rent, Debit, 25_000m), (s.Payables, Credit, 25_000m));             // rent accrued
        await s.PostedAsync((s.Payables, Debit, 25_000m), (s.Bank, Credit, 24_999.99m), (s.Cash, Credit, 0.01m)); // paid
        var mistaken = await s.PostedAsync((s.Rent, Debit, 3_333.33m), (s.Cash, Credit, 3_333.33m));

        await using var connection = db.CreateRuntimeConnection();
        var beforeMistakeReversal = await NetMovementsAsync(connection, s.LedgerId);

        // Correct the mistake by reversal (not mutation).
        var reversal = await s.ReverseAsync(mistaken);
        await s.ApproveAsync(reversal.Id);
        await s.PostAsync(reversal.Id);

        // Noise that must not affect balances: draft, pending, approved-unposted and rejected journals.
        await s.DraftAsync((s.Cash, Debit, 1m), (s.Revenue, Credit, 1m));
        var pending = await s.DraftAsync((s.Cash, Debit, 2m), (s.Revenue, Credit, 2m));
        await s.SubmitAsync(pending);
        await s.ApprovedAsync((s.Cash, Debit, 3m), (s.Revenue, Credit, 3m));
        var rejected = await s.DraftAsync((s.Cash, Debit, 4m), (s.Revenue, Credit, 4m));
        await s.SubmitAsync(rejected);
        await s.RejectAsync(rejected);

        // 1. Every posted journal balances.
        var unbalancedPosted = await Sql.ScalarAsync<long>(
            connection,
            """
            SELECT count(*) FROM (
                SELECT j.id
                  FROM journals j JOIN journal_entries e ON e.journal_id = j.id
                 WHERE j.ledger_id = $1 AND j.status = 'POSTED'
                 GROUP BY j.id
                HAVING sum(e.amount) FILTER (WHERE e.direction = 'DEBIT')
                    <> sum(e.amount) FILTER (WHERE e.direction = 'CREDIT')) unbalanced
            """,
            s.LedgerId);
        Assert.Equal(0L, unbalancedPosted);

        // 2. Across the ledger, total debits equal total credits.
        await using (var totals = new NpgsqlCommand(
            """
            SELECT coalesce(sum(e.amount) FILTER (WHERE e.direction = 'DEBIT'), 0),
                   coalesce(sum(e.amount) FILTER (WHERE e.direction = 'CREDIT'), 0),
                   count(DISTINCT j.id)
              FROM journals j JOIN journal_entries e ON e.journal_id = j.id
             WHERE j.ledger_id = $1 AND j.status = 'POSTED'
            """,
            connection)
        { Parameters = { new() { Value = s.LedgerId } } })
        await using (var reader = await totals.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            var debits = reader.GetDecimal(0);
            var credits = reader.GetDecimal(1);
            Assert.Equal(debits, credits);
            Assert.Equal(100_000m + 25_000m + 25_000m + 3_333.33m + 3_333.33m, debits);
            Assert.Equal(5L, reader.GetInt64(2));
        }

        // 3. Net movement per account (debits − credits) sums to zero, and equals the expected balances.
        var net = await NetMovementsAsync(connection, s.LedgerId);
        Assert.Equal(0m, net.Values.Sum());
        Assert.Equal(75_000.01m, net[s.Bank]);
        Assert.Equal(-0.01m, net[s.Cash]);
        Assert.Equal(0m, net[s.Payables]);
        Assert.Equal(-100_000m, net[s.Revenue]);
        Assert.Equal(25_000m, net[s.Rent]);

        // 4. The reversal returned the affected accounts to their pre-mistake movement.
        Assert.Equal(beforeMistakeReversal[s.Rent] - 3_333.33m, net[s.Rent]);
        Assert.Equal(beforeMistakeReversal[s.Cash] + 3_333.33m, net[s.Cash]);
    }

    private static async Task<Dictionary<Guid, decimal>> NetMovementsAsync(NpgsqlConnection connection, Guid ledgerId)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = new NpgsqlCommand(
            """
            SELECT a.id,
                   coalesce(sum(CASE e.direction WHEN 'DEBIT' THEN e.amount ELSE -e.amount END)
                            FILTER (WHERE j.status = 'POSTED'), 0)
              FROM accounts a
              LEFT JOIN journal_entries e ON e.account_id = a.id
              LEFT JOIN journals j ON j.id = e.journal_id
             WHERE a.ledger_id = $1
             GROUP BY a.id
            """,
            connection)
        { Parameters = { new() { Value = ledgerId } } };
        await using var reader = await command.ExecuteReaderAsync();
        var result = new Dictionary<Guid, decimal>();
        while (await reader.ReadAsync())
        {
            result[reader.GetGuid(0)] = reader.GetDecimal(1);
        }

        return result;
    }
}
