using System.Globalization;
using System.Text.RegularExpressions;
using LedgerCore.Ledger.Api.Application;
using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Persistence;
using LedgerCore.Ledger.Domain;
using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Journals;
using LedgerCore.Ledger.Domain.Monetary;
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

/// <param name="Currency">ISO 4217 code; every entry must use an account in this currency.</param>
/// <param name="Description">What the journal records.</param>
/// <param name="ExternalReference">Optional caller reference, unique per ledger.</param>
/// <param name="TransactionType">Required business classification sent to policy (<c>PAYMENT</c>, <c>TRANSFER</c>, <c>ADJUSTMENT</c>, <c>FEE</c>).</param>
internal sealed record CreateJournalRequest(string Currency, string Description, string? ExternalReference, JournalType? TransactionType);

/// <param name="AccountId">Account in the same ledger and currency as the journal.</param>
/// <param name="Direction"><c>DEBIT</c> or <c>CREDIT</c>.</param>
/// <param name="Amount">Positive decimal string in major units, e.g. "125000.50" (never a JSON number).</param>
/// <param name="Memo">Optional line note.</param>
internal sealed record AddEntryRequest(Guid AccountId, EntryDirection Direction, string Amount, string? Memo);

internal sealed record ReverseJournalRequest(string? Description);

internal sealed record JournalEntryResponse(
    Guid Id, int LineNumber, Guid AccountId, EntryDirection Direction, string Amount, string? Memo);

internal sealed record JournalTotalsResponse(string Debits, string Credits, bool Balanced);

/// <summary>The policy decision recorded as approval evidence (ADR-012).</summary>
internal sealed record PolicyDecisionResponse(
    Guid DecisionId,
    string PolicyVersion,
    string Decision,
    IReadOnlyList<string> ReasonCodes,
    DateTimeOffset EvaluatedAt,
    string ContractVersion,
    DateTimeOffset ReceivedAt)
{
    public static PolicyDecisionResponse From(JournalPolicyDecision d) => new(
        d.DecisionId, d.PolicyVersion, EnumText.ToText(d.Decision), d.ReasonCodes, d.EvaluatedAt, d.ContractVersion, d.ReceivedAt);
}

internal sealed record JournalResponse(
    Guid Id,
    Guid LedgerId,
    string Currency,
    JournalType TransactionType,
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
    string? PostedBy,
    PolicyDecisionResponse? PolicyDecision,
    bool ManualReviewRequired,
    string? ApprovalFailure)
{
    /// <param name="j">The journal.</param>
    /// <param name="reversedBy">The posted reversal of this journal, if any.</param>
    /// <param name="decision">The policy decision recorded for it, if any.</param>
    /// <param name="failure">Set on command responses when no policy decision could be obtained this time.</param>
    public static JournalResponse From(
        Journal j, Guid? reversedBy = null, JournalPolicyDecision? decision = null, PolicyFailureKind? failure = null)
    {
        var totals = DoubleEntry.Totals(j.Entries);
        var minorUnits = j.Currency.MinorUnits;
        return new JournalResponse(
            j.Id,
            j.LedgerId,
            j.Currency.Code,
            j.Type,
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
            j.PostedBy,
            decision is null ? null : PolicyDecisionResponse.From(decision),
            j.Status == JournalStatus.PendingApproval && decision?.Decision == PolicyDecisionValue.ReviewRequired,
            failure is null ? null : EnumText.ToText(failure.Value));
    }

    public static JournalResponse From(JournalView view) => From(view.Journal, view.ReversedByJournalId, view.PolicyDecision);

    public static JournalResponse From(ApprovalOutcome outcome) => From(outcome.Journal, null, outcome.Evidence, outcome.Failure);
}

internal sealed record CurrencyTotalsResponse(string Currency, int PostedJournals, string Debits, string Credits, bool Balanced);

internal sealed record AccountMovementResponse(Guid AccountId, string Code, string Currency, string Debits, string Credits, string Net);

/// <summary>The ledger reconciliation report. Amounts are decimal strings in each currency's minor units.</summary>
internal sealed record ReconciliationResponse(
    Guid LedgerId,
    DateTimeOffset GeneratedAt,
    bool Consistent,
    IReadOnlyList<CurrencyTotalsResponse> Currencies,
    IReadOnlyList<Guid> UnbalancedJournals,
    IReadOnlyList<Guid> MismatchedReversals,
    IReadOnlyList<AccountMovementResponse> Accounts)
{
    public static ReconciliationResponse From(ReconciliationReport r)
    {
        static string Amount(decimal value, string currency) => AmountText.Format(value, Currency.FromCode(currency).MinorUnits);

        return new ReconciliationResponse(
            r.LedgerId,
            r.GeneratedAt,
            r.Consistent,
            [.. r.Currencies.Select(c => new CurrencyTotalsResponse(c.Currency, c.PostedJournals, Amount(c.Debits, c.Currency), Amount(c.Credits, c.Currency), c.Balanced))],
            r.UnbalancedJournals,
            r.MismatchedReversals,
            [.. r.Accounts.Select(a => new AccountMovementResponse(
                a.AccountId, a.Code, a.Currency, Amount(a.Debits, a.Currency), Amount(a.Credits, a.Currency), Amount(a.Net, a.Currency)))]);
    }
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
