using LedgerCore.Ledger.Domain.Accounts;
using LedgerCore.Ledger.Domain.Monetary;

namespace LedgerCore.Ledger.Domain.Tests;

public sealed class AccountTests
{
    private static readonly Currency Kes = Currency.FromCode("KES");

    [Fact]
    public void OpensActiveAccount()
    {
        var account = Account.Open(Guid.CreateVersion7(), "1000", "Cash", AccountType.Asset, Kes, TestLedger.Now);

        Assert.True(account.IsActive);
        Assert.Equal("1000", account.Code);
        Assert.Equal(AccountType.Asset, account.Type);
        Assert.Equal(Kes, account.Currency);
        Assert.Null(account.DeactivatedAt);
    }

    [Fact]
    public void RejectsUndefinedAccountType()
    {
        DomainAssert.Fails(
            "ENUM_INVALID", () => Account.Open(Guid.CreateVersion7(), "1000", "Cash", (AccountType)42, Kes, TestLedger.Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1000")]
    [InlineData("10 00")]
    [InlineData("1000/1")]
    public void RejectsInvalidCodes(string code)
    {
        Assert.Throws<LedgerDomainException>(
            () => Account.Open(Guid.CreateVersion7(), code, "Cash", AccountType.Asset, Kes, TestLedger.Now));
    }

    [Fact]
    public void DeactivationIsOneWayAndRecorded()
    {
        var account = Account.Open(Guid.CreateVersion7(), "1000", "Cash", AccountType.Asset, Kes, TestLedger.Now);

        account.Deactivate(TestLedger.Now);

        Assert.False(account.IsActive);
        Assert.Equal(TestLedger.Now, account.DeactivatedAt);
        DomainAssert.Fails("ACCOUNT_ALREADY_INACTIVE", () => account.Deactivate(TestLedger.Now));
    }
}
