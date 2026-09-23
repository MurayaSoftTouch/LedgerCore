using System.Net;
using LedgerCore.Ledger.Api.Configuration;
using LedgerCore.Ledger.Api.Integration.Correlation;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace LedgerCore.Ledger.Api.Integration.Policy;

/// <summary>
/// HttpClientFactory registration with a bounded resilience pipeline (ADR-012):
/// <code>
/// total timeout (Ledger:PolicyDecisionTimeoutMs, default 2000 ms)
///   └─ retry: at most Ledger:PolicyMaxRetries (default 2) extra attempts, exponential backoff from 100 ms with jitter,
///            only for connection failures, attempt timeouts, 502, 503, 504
///        └─ attempt timeout (Ledger:PolicyAttemptTimeoutMs, default 800 ms)
/// </code>
/// Retrying a POST is safe: the policy service is idempotent on transactionId (ADR-011).
/// </summary>
internal static class PolicyClientRegistration
{
    public const string PipelineName = "policy-decisions";

    public static IHttpClientBuilder AddPolicyDecisionClient(this IServiceCollection services)
    {
        services.AddTransient<CorrelationIdHandler>();
        var builder = services
            .AddHttpClient<IPolicyDecisionClient, PolicyDecisionClient>((provider, http) =>
            {
                var options = provider.GetRequiredService<IOptions<LedgerApiOptions>>().Value;
                http.BaseAddress = new Uri(options.PolicyServiceBaseUrl.TrimEnd('/') + "/");
                // Backstop only; the resilience pipeline's total timeout fires first.
                http.Timeout = TimeSpan.FromMilliseconds(options.PolicyDecisionTimeoutMs + 1000);
            })
            .AddHttpMessageHandler<CorrelationIdHandler>();

        builder.AddResilienceHandler(PipelineName, static (pipeline, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<LedgerApiOptions>>().Value;
            pipeline
                .AddTimeout(TimeSpan.FromMilliseconds(options.PolicyDecisionTimeoutMs))
                .AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = options.PolicyMaxRetries,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromMilliseconds(100),
                    ShouldRetryAfterHeader = false,
                    ShouldHandle = static args => ValueTask.FromResult(IsTransient(args.Outcome)),
                })
                .AddTimeout(TimeSpan.FromMilliseconds(options.PolicyAttemptTimeoutMs));
        });

        return builder;
    }

    internal static bool IsTransient(Outcome<HttpResponseMessage> outcome) => outcome switch
    {
        { Exception: HttpRequestException } => true,
        { Exception: TimeoutRejectedException } => true,
        { Result.StatusCode: HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout } => true,
        _ => false,
    };
}
