using LedgerCore.Ledger.Api.Integration.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LedgerCore.Ledger.Api.Tests.Infrastructure;

/// <summary>
/// The real API host, connected to the test container as the runtime role. The policy service is
/// replaced by <see cref="Policy"/> unless <c>useRealPolicyClient</c> is set.
/// </summary>
public sealed class LedgerApiFactory(PostgresFixture postgres, bool useRealPolicyClient = false) : WebApplicationFactory<Program>
{
    public const string TestPolicyToken = "test-ledger-policy-token-000000000000000000";

    // RFC 5737 TEST-NET-1: never routed, so no local service (such as the Compose policy service on
    // the appsettings default, localhost:8081) can answer the test host.
    public const string UnreachablePolicyServiceBaseUrl = "http://192.0.2.1:8081";

    internal StubPolicyDecisionClient Policy { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Ledger", postgres.RuntimeConnectionString);
        builder.UseSetting("Ledger:PolicyServiceToken", TestPolicyToken);
        builder.UseSetting("Ledger:PolicyServiceBaseUrl", UnreachablePolicyServiceBaseUrl);
        if (!useRealPolicyClient)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPolicyDecisionClient>();
                services.AddSingleton<IPolicyDecisionClient>(Policy);
            });
        }
    }
}
