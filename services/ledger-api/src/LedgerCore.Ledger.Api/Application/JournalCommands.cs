using System.Diagnostics;
using LedgerCore.Ledger.Api.Integration.Correlation;
using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Persistence;
using LedgerCore.Ledger.Domain;
using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Journals;
using LedgerCore.Ledger.Domain.Monetary;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LedgerCore.Ledger.Api.Application;

/// <param name="Journal">The command's result journal, as persisted.</param>
/// <param name="Replayed">True when an earlier request with the same key and fingerprint produced it.</param>
internal sealed record CommandResult(Journal Journal, bool Replayed);

/// <summary>
/// Journal state changes. Each command runs in one database transaction that locks the journal row
/// before reading it; the domain validates, and the database triggers re-validate on write (ADR-007).
/// </summary>
internal sealed partial class JournalCommands(LedgerDbContext db, TimeProvider time, ILogger<JournalCommands>? logger = null)
{
    public async Task<Journal> CreateDraftAsync(
        Guid ledgerId,
        string currency,
        JournalType type,
        string description,
        string? externalReference,
        string actor,
        CancellationToken ct)
    {
        if (!await db.Ledgers.AnyAsync(l => l.Id == ledgerId, ct))
        {
            throw NotFound.Ledger(ledgerId);
        }

        var journal = Journal.CreateDraft(
            ledgerId, Currency.FromCode(currency), type, description, externalReference, actor, time.GetUtcNow());
        db.Journals.Add(journal);
        await db.SaveChangesAsync(ct);
        return journal;
    }

    public Task<Journal> AddEntryAsync(
        Guid ledgerId, Guid journalId, Guid accountId, EntryDirection direction, decimal amount, string? memo, CancellationToken ct) =>
        InTransactionAsync(ledgerId, journalId, async journal =>
        {
            var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId && a.LedgerId == ledgerId, ct)
                ?? throw new LedgerDomainException(
                    DomainErrorKind.RuleViolation, "ACCOUNT_NOT_FOUND", $"Account {accountId} does not exist in this ledger.");
            journal.AddEntry(account, direction, Money.Of(amount, journal.Currency), memo);
        }, ct);

    public Task<Journal> SubmitAsync(Guid ledgerId, Guid journalId, string actor, CancellationToken ct) =>
        InTransactionAsync(ledgerId, journalId, journal =>
        {
            journal.Submit(actor, time.GetUtcNow());
            return Task.CompletedTask;
        }, ct);

    /// <summary>
    /// Posts an approved journal, idempotently (ADR-015). Inside one transaction: lock the journal,
    /// replay if this key already posted it, claim the key, re-read the persisted entries and
    /// share-lock the accounts, validate, transition, and write the <c>JournalPosted</c> outbox
    /// event bound to the recorded APPROVED decision (ADR-014). Client totals are never used and the
    /// policy service is never called: posting relies only on persisted approval evidence.
    /// </summary>
    public Task<CommandResult> PostAsync(Guid ledgerId, Guid journalId, string actor, IdempotencyKey key, CancellationToken ct) =>
        IdempotentAsync(
            ledgerId,
            journalId,
            CommandOperation.PostJournal,
            key,
            CommandFingerprint.ForPosting(ledgerId, journalId, actor),
            actor,
            prepare: journal =>
            {
                if (journal.Status == JournalStatus.Posted)
                {
                    // Posted before, under another key (or before keys existed): never a second posting.
                    throw new LedgerDomainException(
                        DomainErrorKind.InvalidState, "JOURNAL_ALREADY_POSTED", "Journal is already posted.");
                }

                return Task.FromResult(journal);
            },
            complete: async (journal, _) =>
            {
                var accounts = await LockAndLoadAccountsAsync(journal, ct);
                var totals = journal.Post(accounts, actor, time.GetUtcNow());
                var evidence = await db.JournalPolicyDecisions.AsNoTracking().SingleOrDefaultAsync(d => d.JournalId == journalId, ct);
                if (evidence?.Decision != PolicyDecisionValue.Approved)
                {
                    // Unreachable through the application (APPROVED requires this evidence); the
                    // database refuses it too. Kept so posting never depends on that reasoning alone.
                    throw new LedgerDomainException(
                        DomainErrorKind.InvalidState, "APPROVAL_EVIDENCE_REQUIRED", $"Journal {journalId} has no recorded APPROVED policy decision.");
                }

                db.OutboxEvents.Add(OutboxEvent.JournalPosted(journal, totals, evidence.DecisionId));
            },
            ct);

    /// <summary>
    /// Creates the reversal of a posted journal, idempotently and atomically: lock the original,
    /// replay if this key already reversed it, refuse if another live reversal exists, insert the
    /// mirrored draft, claim the key with the reversal as its result, submit it, commit. The
    /// original row is not written.
    /// </summary>
    public Task<CommandResult> ReverseAsync(
        Guid ledgerId, Guid journalId, string? description, string actor, IdempotencyKey key, CancellationToken ct) =>
        IdempotentAsync(
            ledgerId,
            journalId,
            CommandOperation.ReverseJournal,
            key,
            CommandFingerprint.ForReversal(ledgerId, journalId, actor, description),
            actor,
            prepare: async original =>
            {
                var existing = await db.Journals
                    .Where(j => j.ReversesJournalId == journalId && j.Status != JournalStatus.Rejected)
                    .Select(j => (Guid?)j.Id)
                    .FirstOrDefaultAsync(ct);
                if (existing is not null)
                {
                    throw new LedgerDomainException(
                        DomainErrorKind.Conflict, "JOURNAL_ALREADY_REVERSED", $"Journal {journalId} already has reversal {existing}.");
                }

                var accounts = await LockAndLoadAccountsAsync(original, ct);
                var reversal = Journal.CreateReversal(original, accounts, description, actor, time.GetUtcNow());
                db.Journals.Add(reversal);
                await db.SaveChangesAsync(ct);
                return reversal;
            },
            complete: (_, reversal) =>
            {
                reversal.Submit(actor, reversal.CreatedAt);
                return Task.CompletedTask;
            },
            ct);

    /// <summary>
    /// The idempotency protocol shared by posting and reversal (ADR-015), in one transaction:
    /// <list type="number">
    /// <item>Lock the target journal. Requests for the same journal now run one at a time.</item>
    /// <item>If a claim for (ledger, operation, key) is committed: same fingerprint → replay its
    /// result from persisted state, writing nothing; different fingerprint → IDEMPOTENCY_CONFLICT.</item>
    /// <item><paramref name="prepare"/> validates and returns the result journal (inserting it, for a reversal).</item>
    /// <item>Claim the key with <c>INSERT … ON CONFLICT DO NOTHING</c>. The primary key is the
    /// authority: a concurrent claim of the same key waits here, and if it commits we roll back and
    /// re-run, which then takes step 2.</item>
    /// <item><paramref name="complete"/> performs the effect; commit.</item>
    /// </list>
    /// Any failure rolls back the claim with the effect, so a key never outlives a failed attempt.
    /// </summary>
    private async Task<CommandResult> IdempotentAsync(
        Guid ledgerId,
        Guid targetJournalId,
        CommandOperation operation,
        IdempotencyKey key,
        string fingerprint,
        string actor,
        Func<Journal, Task<Journal>> prepare,
        Func<Journal, Journal, Task> complete,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var operationText = EnumText.ToText(operation);
        var keyReference = key.Reference;
        try
        {
            var result = await RunIdempotentAsync(ledgerId, targetJournalId, operation, key, fingerprint, actor, prepare, complete, ct);
            var outcome = result.Replayed ? "REPLAYED" : "COMPLETED";
            var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Log.CommandCompleted(Logger, operationText, ledgerId, targetJournalId, result.Journal.Id, outcome, keyReference, durationMs);
            return result;
        }
        catch (LedgerDomainException e)
        {
            var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Log.CommandRefused(Logger, operationText, ledgerId, targetJournalId, e.Code, keyReference, durationMs);
            throw;
        }
    }

    private ILogger Logger => logger ?? (ILogger)NullLogger.Instance;

    private async Task<CommandResult> RunIdempotentAsync(
        Guid ledgerId,
        Guid targetJournalId,
        CommandOperation operation,
        IdempotencyKey key,
        string fingerprint,
        string actor,
        Func<Journal, Task<Journal>> prepare,
        Func<Journal, Journal, Task> complete,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.LockJournalAsync(ledgerId, targetJournalId, ct);
            var target = await LoadAsync(ledgerId, targetJournalId, ct);

            var claim = await db.CommandIdempotency.AsNoTracking()
                .SingleOrDefaultAsync(c => c.LedgerId == ledgerId && c.Operation == operation && c.IdempotencyKey == key.Value, ct);
            if (claim is not null)
            {
                if (claim.RequestFingerprint != fingerprint)
                {
                    throw IdempotencyConflict(operation);
                }

                var replayed = claim.ResultJournalId == target.Id ? target : await LoadAsync(ledgerId, claim.ResultJournalId, ct);
                return new CommandResult(replayed, Replayed: true);
            }

            var result = await prepare(target);
            if (!await TryClaimAsync(ledgerId, operation, key, fingerprint, target.Id, result.Id, actor, ct))
            {
                // A concurrent request committed this key for another journal while we held ours.
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                if (attempt == 1)
                {
                    continue;
                }

                throw IdempotencyConflict(operation);
            }

            await complete(target, result);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Answer from what was persisted (timestamps at PostgreSQL's microsecond precision), so
            // the first response and every replay of it are identical.
            db.ChangeTracker.Clear();
            return new CommandResult(await LoadAsync(ledgerId, result.Id, ct), Replayed: false);
        }
    }

    private async Task<bool> TryClaimAsync(
        Guid ledgerId,
        CommandOperation operation,
        IdempotencyKey key,
        string fingerprint,
        Guid targetJournalId,
        Guid resultJournalId,
        string actor,
        CancellationToken ct)
    {
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO command_idempotency
                (ledger_id, operation, idempotency_key, request_fingerprint, target_journal_id, result_journal_id, requested_by, correlation_id, created_at)
            VALUES ({ledgerId}, {EnumText.ToText(operation)}, {key.Value}, {fingerprint}, {targetJournalId}, {resultJournalId}, {actor}, {CorrelationId.Current}, {time.GetUtcNow()})
            ON CONFLICT ON CONSTRAINT pk_command_idempotency DO NOTHING
            """,
            ct);
        return inserted == 1;
    }

    private static LedgerDomainException IdempotencyConflict(CommandOperation operation) =>
        new(
            DomainErrorKind.Conflict,
            "IDEMPOTENCY_CONFLICT",
            $"This {IdempotencyKey.Header} was already used for a different {EnumText.ToText(operation)} request. Use a new key for a new request.");

    internal async Task<Journal> InTransactionAsync(
        Guid ledgerId, Guid journalId, Func<Journal, Task> change, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockJournalAsync(ledgerId, journalId, ct);
        var journal = await LoadAsync(ledgerId, journalId, ct);

        await change(journal);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return journal;
    }

    private async Task<Journal> LoadAsync(Guid ledgerId, Guid journalId, CancellationToken ct) =>
        await db.Journals.Include(j => j.Entries)
            .SingleOrDefaultAsync(j => j.Id == journalId && j.LedgerId == ledgerId, ct)
        ?? throw NotFound.Journal(journalId);

    private async Task<Dictionary<Guid, Account>> LockAndLoadAccountsAsync(Journal journal, CancellationToken ct)
    {
        var accountIds = journal.Entries.Select(e => e.AccountId).Distinct().Order().ToArray();
        await db.LockAccountsForShareAsync(accountIds, ct);
        return await db.Accounts.Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
    }

    /// <summary>Identifiers, outcome and duration only; the key appears only as <see cref="IdempotencyKey.Reference"/>.</summary>
    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "{Operation} on journal {JournalId} in ledger {LedgerId} -> {ResultJournalId}: {Outcome} (key {IdempotencyKeyRef}) in {DurationMs} ms")]
        public static partial void CommandCompleted(ILogger logger, string operation, Guid ledgerId, Guid journalId, Guid resultJournalId, string outcome, string idempotencyKeyRef, long durationMs);

        [LoggerMessage(Level = LogLevel.Information, Message = "{Operation} on journal {JournalId} in ledger {LedgerId} refused: {Code} (key {IdempotencyKeyRef}) in {DurationMs} ms")]
        public static partial void CommandRefused(ILogger logger, string operation, Guid ledgerId, Guid journalId, string code, string idempotencyKeyRef, long durationMs);
    }
}
