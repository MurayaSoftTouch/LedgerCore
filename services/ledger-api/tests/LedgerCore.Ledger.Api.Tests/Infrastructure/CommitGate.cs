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
