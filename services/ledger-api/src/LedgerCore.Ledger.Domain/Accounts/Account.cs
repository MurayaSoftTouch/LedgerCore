using LedgerCore.Ledger.Domain.Monetary;

namespace LedgerCore.Ledger.Domain.Accounts;

/// <summary>
/// A chart-of-accounts entry. Identity, type and currency never change once opened; an account
/// referenced by history is deactivated, never deleted.
/// </summary>
public sealed class Account
{
    private Account()
    {
    }

    public Guid Id { get; private set; }

    public Guid LedgerId { get; private set; }

    public string Code { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public AccountType Type { get; private set; }

    public Currency Currency { get; private set; } = null!;

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DeactivatedAt { get; private set; }

    public static Account Open(
        Guid ledgerId, string code, string name, AccountType type, Currency currency, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(currency);

        return new Account
        {
            Id = Guid.CreateVersion7(now),
            LedgerId = ledgerId,
            Code = Guard.Code(code, "code"),
            Name = Guard.Text(name, "name", 200),
            Type = Guard.Defined(type, "type"),
            Currency = currency,
            IsActive = true,
            CreatedAt = now,
        };
    }

    public void Deactivate(DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new LedgerDomainException(
                DomainErrorKind.InvalidState, "ACCOUNT_ALREADY_INACTIVE", $"Account {Code} is already inactive.");
        }

        IsActive = false;
        DeactivatedAt = now;
    }
}
