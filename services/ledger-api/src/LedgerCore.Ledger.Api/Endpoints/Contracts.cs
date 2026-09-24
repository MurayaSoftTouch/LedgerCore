using System.Globalization;
using System.Text.RegularExpressions;
using LedgerCore.Ledger.Api.Application;
using LedgerCore.Ledger.Domain;
using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Journals;
using DomainLedger = LedgerCore.Ledger.Domain.Ledgers.Ledger;

namespace LedgerCore.Ledger.Api.Endpoints;

internal sealed record CreateLedgerRequest(string Code, string Name);

internal sealed record LedgerResponse(Guid Id, string Code, string Name, DateTimeOffset CreatedAt)
{
    public static LedgerResponse From(DomainLedger l) => new(l.Id, l.Code, l.Name, l.CreatedAt);
}

internal sealed record OpenAccountRequest(string Code, string Name, AccountType Type, string Currency);

internal sealed record AccountResponse(
    Guid Id,
    Guid LedgerId,
    string Code,
    string Name,
    AccountType Type,
    string Currency,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeactivatedAt)
{
    public static AccountResponse From(Account a) =>
        new(a.Id, a.LedgerId, a.Code, a.Name, a.Type, a.Currency.Code, a.IsActive, a.CreatedAt, a.DeactivatedAt);
}

internal sealed record CreateJournalRequest(string Currency, string Description, string? ExternalReference);

/// <param name="AccountId">Account in the same ledger and currency as the journal.</param>
/// <param name="Direction"><c>DEBIT</c> or <c>CREDIT</c>.</param>
/// <param name="Amount">Positive decimal string in major units, e.g. "125000.50" (never a JSON number).</param>
/// <param name="Memo">Optional line note.</param>
internal sealed record AddEntryRequest(Guid AccountId, EntryDirection Direction, string Amount, string? Memo);

internal sealed record ReverseJournalRequest(string? Description);

internal sealed record JournalEntryResponse(
    Guid Id, int LineNumber, Guid AccountId, EntryDirection Direction, string Amount, string? Memo);

internal sealed record JournalTotalsResponse(string Debits, string Credits, bool Balanced);

internal sealed record JournalResponse(
    Guid Id,
    Guid LedgerId,
    string Currency,
    string Description,
    string? ExternalReference,
    JournalStatus Status,
    Guid? ReversesJournalId,
    bool IsReversed,
    Guid? ReversedByJournalId,
    JournalTotalsResponse Totals,
    IReadOnlyList<JournalEntryResponse> Entries,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? SubmittedAt,
    string? SubmittedBy,
    DateTimeOffset? ApprovedAt,
    string? ApprovedBy,
    DateTimeOffset? RejectedAt,
    string? RejectedBy,
    string? RejectionReason,
    DateTimeOffset? PostedAt,
    string? PostedBy)
{
    public static JournalResponse From(Journal j, Guid? reversedBy = null)
    {
        var totals = DoubleEntry.Totals(j.Entries);
        var minorUnits = j.Currency.MinorUnits;
        return new JournalResponse(
            j.Id,
            j.LedgerId,
            j.Currency.Code,
            j.Description,
            j.ExternalReference,
            j.Status,
            j.ReversesJournalId,
            reversedBy is not null,
            reversedBy,
            new JournalTotalsResponse(
                AmountText.Format(totals.Debits, minorUnits), AmountText.Format(totals.Credits, minorUnits), totals.IsBalanced),
            [.. j.Entries.OrderBy(e => e.LineNumber).Select(e => new JournalEntryResponse(
                e.Id, e.LineNumber, e.AccountId, e.Direction, AmountText.Format(e.Amount, minorUnits), e.Memo))],
            j.CreatedAt,
            j.CreatedBy,
            j.SubmittedAt,
            j.SubmittedBy,
            j.ApprovedAt,
            j.ApprovedBy,
            j.RejectedAt,
            j.RejectedBy,
            j.RejectionReason,
            j.PostedAt,
            j.PostedBy);
    }

    public static JournalResponse From(JournalView view) => From(view.Journal, view.ReversedByJournalId);
}

/// <summary>Amounts cross the API as decimal strings, matching the policy contract (ADR-005).</summary>
internal static partial class AmountText
{
    public static decimal Parse(string? text)
    {
        if (text is null || !AmountPattern().IsMatch(text)
            || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            throw new LedgerDomainException(
                DomainErrorKind.Invalid,
                "AMOUNT_FORMAT_INVALID",
                "amount must be a positive decimal string with at most 18 integer digits and 4 decimals, e.g. \"125000.50\".");
        }

        return value;
    }

    public static string Format(decimal amount, int minorUnits) =>
        amount.ToString("F" + minorUnits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(0|[1-9]\d{0,17})(\.\d{1,4})?$", RegexOptions.CultureInvariant)]
    private static partial Regex AmountPattern();
}
