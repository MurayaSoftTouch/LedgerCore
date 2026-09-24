using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LedgerCore.Ledger.Api.Tests.Infrastructure;

/// <summary>Fails every transaction commit on the context it is attached to: all SQL has run, then commit throws.</summary>
internal sealed class FailingCommitInterceptor : DbTransactionInterceptor
{
    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Injected failure before commit.");
}

/// <summary>
/// Pauses a transaction just before COMMIT (all its statements, and so its row locks, are in place)
/// until <see cref="Release"/> is called. Used to observe other transactions blocking on those locks.
/// </summary>
internal sealed class PausingCommitInterceptor : DbTransactionInterceptor
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Reached => _reached.Task;

    public void Release() => _release.TrySetResult();

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        _reached.TrySetResult();
        await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        return result;
    }
}

/// <summary>
/// Fails the first command whose SQL contains <paramref name="sqlFragment"/>, before it reaches the
/// server: a failure injected at one statement boundary inside a transaction.
/// </summary>
internal sealed class FailingCommandInterceptor(string sqlFragment) : DbCommandInterceptor
{
    private int _failed;

    public bool Fired => _failed == 1;

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(command);
        return ValueTask.FromResult(result);
    }

    private void Check(DbCommand command)
    {
        if (command.CommandText.Contains(sqlFragment, StringComparison.Ordinal) && Interlocked.Exchange(ref _failed, 1) == 0)
        {
            throw new InvalidOperationException($"Injected failure at: {sqlFragment}");
        }
    }
}

internal static class Injected
{
    /// <summary>Asserts the action failed because of an injected failure (EF may wrap it in DbUpdateException).</summary>
    public static async Task FailureAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(action);
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            if (e is InvalidOperationException && e.Message.StartsWith("Injected failure", StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Fail($"Expected an injected failure, got: {error}");
    }
}
