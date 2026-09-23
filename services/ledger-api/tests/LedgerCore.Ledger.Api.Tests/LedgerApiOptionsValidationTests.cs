using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>Startup validation happens before any database access, so these need no container.</summary>
public sealed class LedgerApiOptionsValidationTests
{
    private const string UnusedConnection = "Host=unused.invalid;Database=ledger;Username=ledger_runtime";
    private const string ValidToken = "options-test-policy-token-00000000000000000";

    [Theory]
    [InlineData("Ledger:ContractVersion", "")]
    [InlineData("Ledger:ContractVersion", "v1")]
    [InlineData("Ledger:PolicyServiceBaseUrl", "not-a-url")]
    [InlineData("Ledger:PolicyDecisionTimeoutMs", "0")]
    [InlineData("Ledger:PolicyAttemptTimeoutMs", "0")]
    [InlineData("Ledger:PolicyMaxRetries", "9")]
    [InlineData("Ledger:PolicyServiceToken", "too-short")]
    public void HostRefusesToStartWithInvalidConfiguration(string key, string value)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Ledger", UnusedConnection)
            .UseSetting("Ledger:PolicyServiceToken", ValidToken)
            .UseSetting(key, value));

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(key.Split(':')[1], exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HostRefusesToStartWithoutLedgerConnectionString()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Ledger", string.Empty)
            .UseSetting("Ledger:PolicyServiceToken", ValidToken));

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("Ledger", exception.Message, StringComparison.Ordinal);
    }
}
