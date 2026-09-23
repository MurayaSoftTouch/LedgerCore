namespace LedgerCore.Ledger.Domain.Tests;

internal static class DomainAssert
{
    public static LedgerDomainException Fails(string expectedCode, Action action)
    {
        var exception = Assert.Throws<LedgerDomainException>(action);
        Assert.Equal(expectedCode, exception.Code);
        return exception;
    }
}
