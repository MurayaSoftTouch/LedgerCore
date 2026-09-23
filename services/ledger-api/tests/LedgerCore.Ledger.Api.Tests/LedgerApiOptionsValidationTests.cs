using LedgerCore.Ledger.Api.Configuration;
using LedgerCore.Ledger.Api.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LedgerCore.Ledger.Api.Tests;

/// <summary>
/// Startup validation of the host's real option registration, over the real appsettings.json. It runs
/// the <see cref="IStartupValidator"/> that host start invokes, not a WebApplicationFactory. With
/// minimal hosting the factory races the failing entry point, and it may surface
/// ObjectDisposedException instead of the validation error.
/// </summary>
public sealed class LedgerApiOptionsValidationTests
{
    private const string UnusedConnection = "Host=unused.invalid;Database=ledger;Username=ledger_runtime";
    private const string ValidToken = "options-test-policy-token-00000000000000000";

    private static void ValidateAtStartup(params (string Key, string? Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ledger"] = UnusedConnection,
            ["Ledger:PolicyServiceToken"] = ValidToken,
        };
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(RepositoryPaths.LedgerApiAppSettings)
            .AddInMemoryCollection(settings)
            .Build();
        using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLedgerOptions()
            .BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void ValidConfigurationPasses() => ValidateAtStartup();

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
        var exception = Assert.Throws<OptionsValidationException>(() => ValidateAtStartup((key, value)));
        Assert.Contains(key.Split(':')[1], exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HostRefusesToStartWithoutLedgerConnectionString()
    {
        var exception = Assert.Throws<OptionsValidationException>(() => ValidateAtStartup(("ConnectionStrings:Ledger", string.Empty)));
        Assert.Contains("Ledger", exception.Message, StringComparison.Ordinal);
    }
}
