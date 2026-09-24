using LedgerCore.Ledger.Api.Application;
using LedgerCore.Ledger.Domain;

namespace LedgerCore.Ledger.Api.Endpoints;

/// <summary>
/// Explicit commands only. There is deliberately no generic update (PATCH) and no delete: journal state
/// changes through named transitions, and posted history has no mutation endpoint at all.
/// </summary>
internal static class LedgerEndpoints
{
    public static void MapLedgerEndpoints(this IEndpointRouteBuilder app)
    {
        var ledgers = app.MapGroup("/api/v1/ledgers").WithTags("ledgers");

        ledgers.MapPost("/", async (CreateLedgerRequest request, AccountCommands commands, CancellationToken ct) =>
        {
            var ledger = await commands.CreateLedgerAsync(request.Code, request.Name, ct);
            return Results.Created($"/api/v1/ledgers/{ledger.Id}", LedgerResponse.From(ledger));
        });

        ledgers.MapGet("/{ledgerId:guid}", async (Guid ledgerId, LedgerQueries queries, CancellationToken ct) =>
            LedgerResponse.From(await queries.GetLedgerAsync(ledgerId, ct)));

        // Read-only consistency report over POSTED entries (Milestone 4). "consistent" is false only if
        // something bypassed the ledger's database guards.
        ledgers.MapGet("/{ledgerId:guid}/reconciliation", async (Guid ledgerId, LedgerReconciliation reconciliation, CancellationToken ct) =>
            ReconciliationResponse.From(await reconciliation.RunAsync(ledgerId, ct)));

        var accounts = ledgers.MapGroup("/{ledgerId:guid}/accounts").WithTags("accounts");

        accounts.MapPost("/", async (Guid ledgerId, OpenAccountRequest request, AccountCommands commands, CancellationToken ct) =>
        {
            var account = await commands.OpenAccountAsync(
                ledgerId, request.Code, request.Name, request.Type, request.Currency, ct);
            return Results.Created($"/api/v1/ledgers/{ledgerId}/accounts/{account.Id}", AccountResponse.From(account));
        });

        accounts.MapGet("/", async (Guid ledgerId, LedgerQueries queries, CancellationToken ct) =>
            (await queries.ListAccountsAsync(ledgerId, ct)).Select(AccountResponse.From));

        accounts.MapGet("/{accountId:guid}", async (Guid ledgerId, Guid accountId, LedgerQueries queries, CancellationToken ct) =>
            AccountResponse.From(await queries.GetAccountAsync(ledgerId, accountId, ct)));

        accounts.MapPost("/{accountId:guid}/deactivate", async (Guid ledgerId, Guid accountId, AccountCommands commands, CancellationToken ct) =>
            AccountResponse.From(await commands.DeactivateAccountAsync(ledgerId, accountId, ct)));

        var journals = ledgers.MapGroup("/{ledgerId:guid}/journals").WithTags("journals");

        journals.MapPost("/", async (Guid ledgerId, CreateJournalRequest request, JournalCommands commands, HttpContext http, CancellationToken ct) =>
        {
            var type = request.TransactionType ?? throw new LedgerDomainException(
                DomainErrorKind.Invalid, "TRANSACTION_TYPE_REQUIRED", "transactionType is required.");
            var journal = await commands.CreateDraftAsync(
                ledgerId, request.Currency, type, request.Description, request.ExternalReference, Actor.From(http), ct);
            return Results.Created($"/api/v1/ledgers/{ledgerId}/journals/{journal.Id}", JournalResponse.From(journal));
        });

        journals.MapGet("/{journalId:guid}", async (Guid ledgerId, Guid journalId, LedgerQueries queries, CancellationToken ct) =>
            JournalResponse.From(await queries.GetJournalAsync(ledgerId, journalId, ct)));

        journals.MapPost("/{journalId:guid}/entries", async (Guid ledgerId, Guid journalId, AddEntryRequest request, JournalCommands commands, HttpContext http, CancellationToken ct) =>
        {
            _ = Actor.From(http);
            var journal = await commands.AddEntryAsync(
                ledgerId, journalId, request.AccountId, request.Direction, AmountText.Parse(request.Amount), request.Memo, ct);
            return Results.Created($"/api/v1/ledgers/{ledgerId}/journals/{journalId}", JournalResponse.From(journal));
        });

        // Submit commits PENDING_APPROVAL first, then asks for a policy decision. A policy failure does
        // not undo the submission: the journal stays pending and approvalFailure says why.
        journals.MapPost("/{journalId:guid}/submit", async (Guid ledgerId, Guid journalId, JournalCommands commands, PolicyApproval approval, HttpContext http, CancellationToken ct) =>
        {
            var actor = Actor.From(http);
            await commands.SubmitAsync(ledgerId, journalId, actor, ct);
            return JournalResponse.From(await approval.RequestAsync(ledgerId, journalId, actor, ct));
        });

        // Retries policy evaluation for a PENDING_APPROVAL journal. Business decisions return 200;
        // a failure to obtain one is a problem response and changes nothing.
        journals.MapPost("/{journalId:guid}/request-approval", async (Guid ledgerId, Guid journalId, PolicyApproval approval, HttpContext http, CancellationToken ct) =>
        {
            var outcome = await approval.RequestAsync(ledgerId, journalId, Actor.From(http), ct);
            return outcome.Failure is { } failure
                ? throw new PolicyApprovalUnavailableException(failure)
                : JournalResponse.From(outcome);
        });

        // Idempotent (ADR-015): requires Idempotency-Key. A retry with the same key and request
        // returns the original posted journal, marked Idempotency-Replayed. Posting uses the recorded
        // approval evidence and never calls the policy service.
        journals.MapPost("/{journalId:guid}/post", async (Guid ledgerId, Guid journalId, JournalCommands commands, LedgerQueries queries, HttpContext http, CancellationToken ct) =>
        {
            var actor = Actor.From(http);
            var result = await commands.PostAsync(ledgerId, journalId, actor, IdempotencyKeyHeader.From(http), ct);
            IdempotencyKeyHeader.MarkReplayed(http, result);
            return JournalResponse.From(result.Journal, null, await queries.GetPolicyDecisionAsync(journalId, ct));
        });

        // Idempotent (ADR-015): a retry with the same key returns the same reversal journal in its
        // current state. The reversal goes through policy like any journal (ADR-006); a retry asks
        // again only while no decision is recorded for it.
        journals.MapPost("/{journalId:guid}/reverse", async (Guid ledgerId, Guid journalId, ReverseJournalRequest? request, JournalCommands commands, PolicyApproval approval, HttpContext http, CancellationToken ct) =>
        {
            var actor = Actor.From(http);
            var result = await commands.ReverseAsync(ledgerId, journalId, request?.Description, actor, IdempotencyKeyHeader.From(http), ct);
            IdempotencyKeyHeader.MarkReplayed(http, result);
            var outcome = await approval.RequestAsync(ledgerId, result.Journal.Id, actor, ct);
            return Results.Created($"/api/v1/ledgers/{ledgerId}/journals/{result.Journal.Id}", JournalResponse.From(outcome));
        });
    }
}
