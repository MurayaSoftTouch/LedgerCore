using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace LedgerCore.Ledger.Api.Tests;

public sealed class LedgerApiOptionsValidationTests
{
    [Theory]
    [InlineData("Ledger:ContractVersion", "")]
    [InlineData("Ledger:ContractVersion", "v1")]
    [InlineData("Ledger:PolicyServiceBaseUrl", "not-a-url")]
    [InlineData("Ledger:PolicyDecisionTimeoutMs", "0")]
    public void HostRefusesToStartWithInvalidConfiguration(string key, string value)
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting(key, value));

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(key.Split(':')[1], exception.Message, StringComparison.Ordinal);
    }
}
