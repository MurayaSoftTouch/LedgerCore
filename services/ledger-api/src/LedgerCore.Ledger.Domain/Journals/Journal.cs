using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Monetary;

namespace LedgerCore.Ledger.Domain.Journals;

/// <summary>
/// A double-entry journal and its lifecycle (ADR-006):
/// DRAFT → PENDING_APPROVAL → APPROVED → POSTED, or PENDING_APPROVAL → REJECTED.
/// Every transition is an explicit method; there is no way to set <see cref="Status"/> directly.
/// </summary>
public sealed class Journal
{
    public const int MaximumEntries = 100;

    private readonly List<JournalEntry> _entries = [];

    private Journal()
    {
    }

    public Guid Id { get; private set; }

    public Guid LedgerId { get; private set; }

    public Currency Currency { get; private set; } = null!;

    public string Description { get; private set; } = null!;

    /// <summary>Caller-supplied reference, unique per ledger, so a retried create cannot duplicate a journal.</summary>
    public string? ExternalReference { get; private set; }

    public JournalStatus Status { get; private set; }

    /// <summary>Set only on a reversal journal: the posted journal it reverses.</summary>
    public Guid? ReversesJournalId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedBy { get; private set; } = null!;

    public DateTimeOffset? SubmittedAt { get; private set; }

    public string? SubmittedBy { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public string? ApprovedBy { get; private set; }

    public DateTimeOffset? RejectedAt { get; private set; }

    public string? RejectedBy { get; private set; }

    public string? RejectionReason { get; private set; }

    public DateTimeOffset? PostedAt { get; private set; }

    public string? PostedBy { get; private set; }

    public IReadOnlyList<JournalEntry> Entries => _entries;

    public bool IsReversal => ReversesJournalId.HasValue;

    public static Journal CreateDraft(
        Guid ledgerId,
        Currency currency,
        string description,
        string? externalReference,
        string actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(currency);

        return new Journal
        {
            Id = Guid.CreateVersion7(now),
            LedgerId = ledgerId,
            Currency = currency,
            Description = Guard.Text(description, "description", 500),
            ExternalReference = Guard.OptionalText(externalReference, "externalReference", 128),
            Status = JournalStatus.Draft,
            CreatedAt = now,
            CreatedBy = Guard.Actor(actor),
        };
    }

    /// <summary>
    /// Creates the reversal of a posted journal: every entry mirrored with its direction swapped,
    /// in the same order, and submitted immediately (the content is derived, never edited).
    /// The original is not modified.
    /// </summary>
    public static Journal CreateReversal(
        Journal original,
        IReadOnlyDictionary<Guid, Account> accounts,
        string? description,
        string actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(accounts);

        if (original.Status != JournalStatus.Posted)
        {
            throw new LedgerDomainException(
                DomainErrorKind.InvalidState,
                "JOURNAL_NOT_POSTED",
                $"Only posted journals can be reversed; journal is {original.Status}.");
        }

        if (original.IsReversal)
        {
            throw new LedgerDomainException(
                DomainErrorKind.InvalidState,
                "REVERSAL_OF_REVERSAL",
                "A reversal journal cannot itself be reversed; post a new correcting journal instead.");
        }

        var reversal = CreateDraft(
            original.LedgerId,
            original.Currency,
            description ?? $"Reversal of journal {original.Id}",
            externalReference: null,
            actor,
            now);
        reversal.ReversesJournalId = original.Id;

        foreach (var entry in original.Entries.OrderBy(e => e.LineNumber))
        {
            reversal.AddEntry(
                ResolveAccount(accounts, entry.AccountId),
                entry.Direction.Opposite(),
                Money.Of(entry.Amount, original.Currency),
                entry.Memo);
        }

        reversal.Submit(actor, now);
        return reversal;
    }

    public JournalEntry AddEntry(Account account, EntryDirection direction, Money amount, string? memo)
    {
        ArgumentNullException.ThrowIfNull(account);
        RequireStatus(JournalStatus.Draft, "add entries");

        if (_entries.Count >= MaximumEntries)
        {
            throw new LedgerDomainException(
                DomainErrorKind.RuleViolation, "JOURNAL_TOO_MANY_ENTRIES", $"A journal allows at most {MaximumEntries} entries.");
        }

        EnsureUsable(account);
        if (amount.Currency != Currency)
        {
            throw CurrencyMismatch(amount.Currency);
        }

        var entry = new JournalEntry(this, _entries.Count + 1, account.Id, direction, amount, memo);
        _entries.Add(entry);
        return entry;
    }

    public void Submit(string actor, DateTimeOffset now)
    {
        var submitter = Guard.Actor(actor);
        RequireStatus(JournalStatus.Draft, "submit");
        DoubleEntry.EnsureBalanced(_entries);

        Status = JournalStatus.PendingApproval;
        SubmittedAt = now;
        SubmittedBy = submitter;
    }

    /// <summary>
    /// Records a trustworthy approval. In Milestone 1 only the internal test approval recorder
    /// calls this; Milestone 2/3 wires it to a policy decision.
    /// </summary>
    public void Approve(string approver, DateTimeOffset now)
    {
        var approvedBy = Guard.Actor(approver);
        RequireStatus(JournalStatus.PendingApproval, "approve");

        Status = JournalStatus.Approved;
        ApprovedAt = now;
        ApprovedBy = approvedBy;
    }

    /// <summary>An explicit business rejection. Operational failures must not call this (ADR-006).</summary>
    public void Reject(string rejecter, string reason, DateTimeOffset now)
    {
        var rejectedBy = Guard.Actor(rejecter);
        var rejectionReason = Guard.Text(reason, "reason", 500);
        RequireStatus(JournalStatus.PendingApproval, "reject");

        Status = JournalStatus.Rejected;
        RejectedAt = now;
        RejectedBy = rejectedBy;
        RejectionReason = rejectionReason;
    }

    /// <summary>
    /// Validates the persisted entries against the current accounts and moves to POSTED.
    /// Callers must supply entries and accounts read inside the posting transaction.
    /// </summary>
    public JournalTotals Post(IReadOnlyDictionary<Guid, Account> accounts, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var postedBy = Guard.Actor(actor);

        if (Status == JournalStatus.Posted)
        {
            throw new LedgerDomainException(
                DomainErrorKind.InvalidState, "JOURNAL_ALREADY_POSTED", "Journal is already posted.");
        }

        RequireStatus(JournalStatus.Approved, "post");
        var totals = DoubleEntry.EnsureBalanced(_entries);
        foreach (var entry in _entries)
        {
            EnsureUsable(ResolveAccount(accounts, entry.AccountId));
        }

        Status = JournalStatus.Posted;
        PostedAt = now;
        PostedBy = postedBy;
        return totals;
    }

    private static Account ResolveAccount(IReadOnlyDictionary<Guid, Account> accounts, Guid accountId) =>
        accounts.TryGetValue(accountId, out var account)
            ? account
            : throw new LedgerDomainException(
                DomainErrorKind.RuleViolation, "ACCOUNT_NOT_FOUND", $"Account {accountId} does not exist.");

    private void EnsureUsable(Account account)
    {
        if (account.LedgerId != LedgerId)
        {
            throw new LedgerDomainException(
                DomainErrorKind.RuleViolation, "ACCOUNT_NOT_IN_LEDGER", $"Account {account.Code} belongs to another ledger.");
        }

        if (account.Currency != Currency)
        {
            throw CurrencyMismatch(account.Currency);
        }

        if (!account.IsActive)
        {
            throw new LedgerDomainException(
                DomainErrorKind.RuleViolation, "ACCOUNT_INACTIVE", $"Account {account.Code} is inactive.");
        }
    }

    private LedgerDomainException CurrencyMismatch(Currency other) =>
        new(DomainErrorKind.RuleViolation, "CURRENCY_MISMATCH", $"Journal currency is {Currency}; got {other}.");

    private void RequireStatus(JournalStatus required, string operation)
    {
        if (Status != required)
        {
            throw new LedgerDomainException(
                DomainErrorKind.InvalidState,
                "JOURNAL_INVALID_STATE",
                $"Cannot {operation} a journal in state {Status}; requires {required}.");
        }
    }
}
