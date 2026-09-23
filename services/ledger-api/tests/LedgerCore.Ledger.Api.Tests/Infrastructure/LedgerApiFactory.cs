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

    internal StubPolicyDecisionClient Policy { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Ledger", postgres.RuntimeConnectionString);
        builder.UseSetting("Ledger:PolicyServiceToken", TestPolicyToken);
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
