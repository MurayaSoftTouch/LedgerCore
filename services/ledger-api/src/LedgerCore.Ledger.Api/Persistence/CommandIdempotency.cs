namespace LedgerCore.Ledger.Api.Persistence;

/// <summary>Commands that take an <c>Idempotency-Key</c> (ADR-015).</summary>
internal enum CommandOperation
{
    /// <summary><c>POST …/journals/{id}/post</c>. The result is the target journal itself.</summary>
    PostJournal,

    /// <summary><c>POST …/journals/{id}/reverse</c>. The result is the reversal journal it created.</summary>
    ReverseJournal,
}

/// <summary>
/// An idempotency claim (ADR-015): the key a client sent for a command, the fingerprint of that
/// request, and what the command produced. It is written in the <b>same transaction</b> as the
/// effect, so it exists exactly when the effect committed. There is no in-progress or failed
/// state, and a rolled-back attempt leaves nothing behind. Append-only: the runtime role may only
/// insert and read, and triggers refuse updates and deletes.
/// </summary>
internal sealed class CommandIdempotency
{
    private CommandIdempotency()
    {
    }

    public Guid LedgerId { get; private set; }

    public CommandOperation Operation { get; private set; }

    public string IdempotencyKey { get; private set; } = null!;

    /// <summary>Lower-case hex SHA-256 of the request's defining fields (see <c>CommandFingerprint</c>).</summary>
    public string RequestFingerprint { get; private set; } = null!;

    /// <summary>The journal the request addressed.</summary>
    public Guid TargetJournalId { get; private set; }

    /// <summary>The journal the command produced: the target for a posting, the reversal for a reversal.</summary>
    public Guid ResultJournalId { get; private set; }

    public string RequestedBy { get; private set; } = null!;

    /// <summary>Tracing only; never part of the fingerprint.</summary>
    public string? CorrelationId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
