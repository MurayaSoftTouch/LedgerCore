using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LedgerCore.Ledger.Api.Tests.Infrastructure;

/// <summary>The real API host, connected to the test container as the runtime role.</summary>
public sealed class LedgerApiFactory(PostgresFixture postgres) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:Ledger", postgres.RuntimeConnectionString);
}
