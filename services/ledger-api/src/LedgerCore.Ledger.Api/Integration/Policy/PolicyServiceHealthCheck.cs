using LedgerCore.Ledger.Api.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace LedgerCore.Ledger.Api.Integration.Policy;

/// <summary>
/// Reports whether the policy service is ready. Exposed on <c>/health/dependencies</c> only: an
/// unavailable policy service degrades approvals (journals stay PENDING_APPROVAL) but does not make
/// the ledger itself unready, since reads, drafting and posting of approved journals still work.
/// </summary>
internal sealed class PolicyServiceHealthCheck(IHttpClientFactory factory, IOptions<LedgerApiOptions> options) : IHealthCheck
{
    public const string ClientName = "policy-health";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(options.Value.PolicyAttemptTimeoutMs));
        try
        {
            var http = factory.CreateClient(ClientName);
            using var response = await http.GetAsync(
                new Uri(new Uri(options.Value.PolicyServiceBaseUrl.TrimEnd('/') + "/"), "actuator/health/readiness"), timeout.Token);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("policy service ready")
                : new HealthCheckResult(context.Registration.FailureStatus, $"policy service readiness returned {(int)response.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "policy service unreachable");
        }
    }
}
