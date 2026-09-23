namespace LedgerCore.Ledger.Domain.Ledgers;

/// <summary>
/// The scope that owns a chart of accounts and its journals. LedgerCore has no organization or
/// tenant model yet; a ledger is the explicit boundary for uniqueness and posting.
/// </summary>
public sealed class Ledger
{
    private Ledger()
    {
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Ledger Create(string code, string name, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Code = Guard.Code(code, "code"),
        Name = Guard.Text(name, "name", 200),
        CreatedAt = now,
    };
}
