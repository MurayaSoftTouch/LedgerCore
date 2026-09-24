using LedgerCore.Ledger.Api.Application;
using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Persistence;
using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Journals;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace LedgerCore.Ledger.Api.Tests.Infrastructure;

/// <summary>
/// Builds a fresh ledger (unique code, so tests never share data) with a small KES chart of
/// accounts, and drives journals through the real application services against PostgreSQL.
/// Every call uses a new DbContext, i.e. a separate connection and transaction.
/// </summary>
internal sealed class LedgerScenario
{
    private readonly PostgresFixture _db;

    private LedgerScenario(PostgresFixture db, Guid ledgerId, Dictionary<string, Guid> accounts)
    {
        _db = db;
        LedgerId = ledgerId;
        Accounts = accounts;
    }

    public Guid LedgerId { get; }

    public IReadOnlyDictionary<string, Guid> Accounts { get; }

    public Guid Cash => Accounts["1000"];

    public Guid Bank => Accounts["1010"];

    public Guid Payables => Accounts["2000"];

    public Guid Revenue => Accounts["4000"];

    public Guid Rent => Accounts["5000"];

    public static async Task<LedgerScenario> CreateAsync(PostgresFixture db, string currency = "KES")
    {
        await using var ctx = db.CreateRuntimeContext();
        var commands = new AccountCommands(ctx, TimeProvider.System);
        var ledger = await commands.CreateLedgerAsync($"T-{Guid.NewGuid():N}"[..32], "Test ledger", default);
        var accounts = new Dictionary<string, Guid>();
        foreach (var (code, type) in new[]
                 {
                     ("1000", AccountType.Asset), ("1010", AccountType.Asset), ("2000", AccountType.Liability),
                     ("4000", AccountType.Revenue), ("5000", AccountType.Expense),
                 })
        {
            var account = await commands.OpenAccountAsync(ledger.Id, code, $"Account {code}", type, currency, default);
            accounts[code] = account.Id;
        }

        return new LedgerScenario(db, ledger.Id, accounts);
    }

    public async Task<Guid> DraftAsync(params (Guid Account, EntryDirection Direction, decimal Amount)[] lines)
    {
        await using var ctx = _db.CreateRuntimeContext();
        var commands = new JournalCommands(ctx, TimeProvider.System);
        var journal = await commands.CreateDraftAsync(LedgerId, "KES", JournalType.Payment, "scenario journal", null, "tester", default);
        foreach (var (account, direction, amount) in lines)
        {
            await commands.AddEntryAsync(LedgerId, journal.Id, account, direction, amount, null, default);
        }

        return journal.Id;
    }

    public async Task<Guid> ApprovedAsync(params (Guid Account, EntryDirection Direction, decimal Amount)[] lines)
    {
        var id = await DraftAsync(lines);
        await SubmitAsync(id);
        await ApproveAsync(id);
        return id;
    }

    public async Task<Guid> PostedAsync(params (Guid Account, EntryDirection Direction, decimal Amount)[] lines)
    {
        var id = await ApprovedAsync(lines);
        await PostAsync(id);
        return id;
    }

    public Task SubmitAsync(Guid journalId) =>
        WithCommands(c => c.SubmitAsync(LedgerId, journalId, "submitter", default));

    /// <summary>Posts with a fresh idempotency key.</summary>
    public async Task<Journal> PostAsync(Guid journalId, params IInterceptor[] interceptors) =>
        (await PostWithKeyAsync(journalId, NewKey(), interceptors)).Journal;

    public Task<CommandResult> PostWithKeyAsync(Guid journalId, string key, params IInterceptor[] interceptors) =>
        PostWithKeyAsync(journalId, key, "poster", interceptors);

    public Task<CommandResult> PostWithKeyAsync(Guid journalId, string key, string actor, params IInterceptor[] interceptors) =>
        WithCommands(c => c.PostAsync(LedgerId, journalId, actor, IdempotencyKey.Parse(key), default), interceptors);

    /// <summary>Reverses with a fresh idempotency key.</summary>
    public async Task<Journal> ReverseAsync(Guid journalId, params IInterceptor[] interceptors) =>
        (await ReverseWithKeyAsync(journalId, NewKey(), null, interceptors)).Journal;

    public Task<CommandResult> ReverseWithKeyAsync(Guid journalId, string key, string? description = null, params IInterceptor[] interceptors) =>
        WithCommands(c => c.ReverseAsync(LedgerId, journalId, description, "reverser", IdempotencyKey.Parse(key), default), interceptors);

    public static string NewKey() => Guid.NewGuid().ToString();

    /// <summary>Stand-in policy service used by <see cref="ApproveAsync"/> and <see cref="RejectAsync"/>.</summary>
    public StubPolicyDecisionClient Policy { get; } = new();

    /// <summary>Approves through the real approval path, with the stub policy deciding APPROVED.</summary>
    public async Task ApproveAsync(Guid journalId)
    {
        Policy.Decide(journalId, PolicyDecisionValue.Approved);
        await RequestApprovalAsync(journalId);
    }

    /// <summary>Rejects through the real approval path, with the stub policy deciding REJECTED.</summary>
    public async Task RejectAsync(Guid journalId)
    {
        Policy.Decide(journalId, PolicyDecisionValue.Rejected, "TEST_REJECTION");
        await RequestApprovalAsync(journalId);
    }

    public async Task<ApprovalOutcome> RequestApprovalAsync(Guid journalId, IPolicyDecisionClient? client = null)
    {
        await using var ctx = _db.CreateRuntimeContext();
        return await new PolicyApproval(ctx, client ?? Policy, TimeProvider.System, NullLogger<PolicyApproval>.Instance)
            .RequestAsync(LedgerId, journalId, "approval-requester", default);
    }

    public async Task DeactivateAsync(Guid accountId)
    {
        await using var ctx = _db.CreateRuntimeContext();
        await new AccountCommands(ctx, TimeProvider.System).DeactivateAccountAsync(LedgerId, accountId, default);
    }

    public async Task<JournalView> GetAsync(Guid journalId)
    {
        await using var ctx = _db.CreateRuntimeContext();
        return await new LedgerQueries(ctx).GetJournalAsync(LedgerId, journalId, default);
    }

    private async Task<T> WithCommands<T>(Func<JournalCommands, Task<T>> action, params IInterceptor[] interceptors)
    {
        await using var ctx = _db.CreateRuntimeContext(interceptors);
        return await action(new JournalCommands(ctx, TimeProvider.System));
    }
}
