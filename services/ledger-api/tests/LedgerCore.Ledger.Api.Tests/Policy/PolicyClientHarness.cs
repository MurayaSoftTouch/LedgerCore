using LedgerCore.Ledger.Api.Configuration;
using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LedgerCore.Ledger.Api.Tests.Policy;

/// <summary>The production client registration (resilience pipeline included) over a stub handler.</summary>
internal static class PolicyClientHarness
{
    public const string Token = "client-test-policy-token-000000000000000000";

    public static readonly Guid TransactionId = Guid.Parse("7f3c2a8e-1d4b-4c6a-9e2f-0b5d8a1c3e7f");

    public static (IPolicyDecisionClient Client, ServiceProvider Provider) Create(
        StubHttpHandler handler, int totalMs = 1_000, int attemptMs = 200, int retries = 2)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<LedgerApiOptions>().Configure(o =>
        {
            typeof(LedgerApiOptions).GetProperty(nameof(o.ContractVersion))!.SetValue(o, "1.1.0");
            typeof(LedgerApiOptions).GetProperty(nameof(o.PolicyServiceBaseUrl))!.SetValue(o, "http://policy.test:8081");
            typeof(LedgerApiOptions).GetProperty(nameof(o.PolicyDecisionTimeoutMs))!.SetValue(o, totalMs);
            typeof(LedgerApiOptions).GetProperty(nameof(o.PolicyAttemptTimeoutMs))!.SetValue(o, attemptMs);
            typeof(LedgerApiOptions).GetProperty(nameof(o.PolicyMaxRetries))!.SetValue(o, retries);
            typeof(LedgerApiOptions).GetProperty(nameof(o.PolicyServiceToken))!.SetValue(o, Token);
        });
        services.AddPolicyDecisionClient().ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IPolicyDecisionClient>(), provider);
    }

    public static PolicyDecisionRequest Request(string totalAmount = "125000.50") => new(
        TransactionId,
        Guid.Parse("0c9e4f21-5a7b-4d3e-8f1a-2b6c9d0e4a5b"),
        "PAYMENT",
        "KES",
        totalAmount,
        [
            new PolicyAccountContext(Guid.Parse("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d"), "EXPENSE", "DEBIT"),
            new PolicyAccountContext(Guid.Parse("b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e"), "ASSET", "CREDIT"),
        ],
        "user-4821",
        new DateTimeOffset(2026, 9, 23, 9, 15, 0, TimeSpan.Zero));

    public static string ExampleResponse(string name) => Contracts.Read(Path.Combine("schemas", "examples", name));
}
