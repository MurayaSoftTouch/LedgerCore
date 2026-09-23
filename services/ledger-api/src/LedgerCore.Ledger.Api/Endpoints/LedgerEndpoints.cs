using LedgerCore.Ledger.Api.Application;

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
            var journal = await commands.CreateDraftAsync(
                ledgerId, request.Currency, request.Description, request.ExternalReference, Actor.From(http), ct);
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

        journals.MapPost("/{journalId:guid}/submit", async (Guid ledgerId, Guid journalId, JournalCommands commands, HttpContext http, CancellationToken ct) =>
            JournalResponse.From(await commands.SubmitAsync(ledgerId, journalId, Actor.From(http), ct)));

        journals.MapPost("/{journalId:guid}/post", async (Guid ledgerId, Guid journalId, JournalCommands commands, HttpContext http, CancellationToken ct) =>
            JournalResponse.From(await commands.PostAsync(ledgerId, journalId, Actor.From(http), ct)));

        journals.MapPost("/{journalId:guid}/reverse", async (Guid ledgerId, Guid journalId, ReverseJournalRequest? request, JournalCommands commands, HttpContext http, CancellationToken ct) =>
        {
            var reversal = await commands.ReverseAsync(ledgerId, journalId, request?.Description, Actor.From(http), ct);
            return Results.Created($"/api/v1/ledgers/{ledgerId}/journals/{reversal.Id}", JournalResponse.From(reversal));
        });
    }
}
