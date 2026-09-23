using System.Globalization;
using System.Text.Json;
using LedgerCore.Ledger.Domain.Journals;

namespace LedgerCore.Ledger.Api.Persistence;

/// <summary>
/// Transactional outbox record (ADR-014): written in the same PostgreSQL transaction as the state
/// change it describes, so an event exists if and only if the change committed. Publishing is not
/// implemented yet; <see cref="PublishedAt"/> is set once by a future relay. Delivery will be
/// at-least-once; consumers must deduplicate on <see cref="Id"/>.
/// </summary>
internal sealed class OutboxEvent
{
    public const string JournalPostedType = "JournalPosted";

    private OutboxEvent()
    {
    }

    public Guid Id { get; private set; }

    public string AggregateType { get; private set; } = null!;

    public Guid AggregateId { get; private set; }

    public string EventType { get; private set; } = null!;

    /// <summary>JSON document; amounts are decimal strings.</summary>
    public string Payload { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public static OutboxEvent JournalPosted(Journal journal, JournalTotals totals, Guid? policyDecisionId)
    {
        var postedAt = journal.PostedAt ?? throw new InvalidOperationException("Journal is not posted.");
        return new OutboxEvent
        {
            Id = Guid.CreateVersion7(postedAt),
            AggregateType = "Journal",
            AggregateId = journal.Id,
            EventType = JournalPostedType,
            CreatedAt = postedAt,
            Payload = JsonSerializer.Serialize(new
            {
                eventVersion = 1,
                journalId = journal.Id,
                ledgerId = journal.LedgerId,
                transactionType = EnumText.ToText(journal.Type),
                currency = journal.Currency.Code,
                totalAmount = totals.Debits.ToString("F" + journal.Currency.MinorUnits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                entryCount = journal.Entries.Count,
                reversesJournalId = journal.ReversesJournalId,
                policyDecisionId,
                postedAt,
                postedBy = journal.PostedBy,
            }),
        };
    }
}
