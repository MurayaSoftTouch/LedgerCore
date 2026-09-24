using Npgsql;

namespace LedgerCore.Ledger.Api.Tests.Infrastructure;

internal static class Locks
{
    /// <summary>
    /// Waits (condition-based, not a fixed sleep) until at least <paramref name="count"/> backends are
    /// blocked waiting for a row/transaction lock.
    /// </summary>
    public static async Task WaitForBlockedBackendsAsync(PostgresFixture db, int count = 1)
    {
        await using var connection = db.CreateOwnerConnection();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var waiting = await Sql.ScalarAsync<long>(
                connection,
                // Row-lock waiters wait on the holder's transactionid lock, which has no database, so
                // this counts all ungranted locks. Database tests run sequentially in one collection.
                "SELECT count(DISTINCT pid) FROM pg_locks WHERE NOT granted");
            if (waiting >= count)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {count} blocked backend(s), saw {waiting}.");
            }

            await Task.Delay(20);
        }
    }
}
