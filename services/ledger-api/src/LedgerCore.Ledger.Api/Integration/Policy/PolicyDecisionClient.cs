using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LedgerCore.Ledger.Api.Configuration;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace LedgerCore.Ledger.Api.Integration.Policy;

/// <summary>
/// Typed client for contract v1 <c>POST /v1/policy-decisions</c> (ADR-005, ADR-012). Retries and
/// timeouts live in the resilience pipeline (<see cref="PolicyClientRegistration"/>); this class
/// maps every outcome to a business decision or an explicit failure and never throws for either.
/// </summary>
internal sealed partial class PolicyDecisionClient(
    HttpClient http, IOptions<LedgerApiOptions> options, ILogger<PolicyDecisionClient> logger) : IPolicyDecisionClient
{
    public const string Path = "v1/policy-decisions";

    public async Task<PolicyEvaluationResult> EvaluateAsync(PolicyDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.Value;
        using var message = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new StringContent(Serialize(request, settings.ContractVersion), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.PolicyServiceToken);

        PolicyEvaluationResult result;
        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            result = Map(response.StatusCode, body, request.TransactionId);
        }
        catch (TimeoutRejectedException)
        {
            result = new PolicyEvaluationResult.Failed(PolicyFailureKind.Timeout, "policy service did not answer within the time budget");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = new PolicyEvaluationResult.Failed(PolicyFailureKind.Timeout, "policy request timed out");
        }
        catch (HttpRequestException e)
        {
            result = new PolicyEvaluationResult.Failed(PolicyFailureKind.Unavailable, $"policy service unreachable ({e.HttpRequestError})");
        }

        if (result is PolicyEvaluationResult.Failed failed)
        {
            LogFailure(request.TransactionId, failed.Kind, failed.Detail);
        }

        return result;
    }

    internal static PolicyEvaluationResult Map(HttpStatusCode status, string body, Guid transactionId) => (int)status switch
    {
        200 => PolicyResponseParser.Parse(body, transactionId),
        401 or 403 => new PolicyEvaluationResult.Failed(PolicyFailureKind.AuthenticationFailed, $"service credential refused ({(int)status})"),
        409 when PolicyResponseParser.ProblemCode(body) == "IDEMPOTENCY_CONFLICT" =>
            new PolicyEvaluationResult.Failed(PolicyFailureKind.Conflict, "transaction already decided with different inputs"),
        409 or 400 or 422 => new PolicyEvaluationResult.Failed(
            PolicyFailureKind.ContractViolation, $"policy service refused the request ({(int)status} {PolicyResponseParser.ProblemCode(body) ?? "no code"})"),
        >= 500 and <= 599 => new PolicyEvaluationResult.Failed(PolicyFailureKind.Unavailable, $"policy service error ({(int)status})"),
        _ => new PolicyEvaluationResult.Failed(PolicyFailureKind.InvalidResponse, $"unexpected status {(int)status}"),
    };

    /// <summary>Contract v1 request body. Amounts stay decimal strings.</summary>
    internal static string Serialize(PolicyDecisionRequest request, string contractVersion) =>
        JsonSerializer.Serialize(new
        {
            contractVersion,
            transactionId = request.TransactionId,
            organizationId = request.OrganizationId,
            transactionType = request.TransactionType,
            currency = request.Currency,
            totalAmount = request.TotalAmount,
            accountContext = request.AccountContext.Select(a => new { accountId = a.AccountId, accountType = a.AccountType, side = a.Side }),
            requestedBy = new { principalId = request.RequestedBy, principalType = "USER" },
            requestedAt = request.RequestedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        });

    [LoggerMessage(Level = LogLevel.Warning, Message = "Policy evaluation failed for transaction {PolicyTransactionId}: {FailureKind} ({Detail})")]
    private partial void LogFailure(Guid policyTransactionId, PolicyFailureKind failureKind, string detail);
}
