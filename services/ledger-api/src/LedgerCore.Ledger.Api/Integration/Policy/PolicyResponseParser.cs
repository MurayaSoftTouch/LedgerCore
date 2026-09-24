using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LedgerCore.Ledger.Api.Integration.Policy;

/// <summary>
/// Strict reader for contract v1 decision responses. Every required field must be present with the
/// right type and format; unknown fields are ignored and can never change the outcome.
/// </summary>
internal static partial class PolicyResponseParser
{
    public static PolicyEvaluationResult Parse(string body, Guid expectedTransactionId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Invalid("response body is not valid JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Violation("response is not a JSON object");
            }

            if (!String(root, "contractVersion", out var contractVersion) || !ContractVersionPattern().IsMatch(contractVersion))
            {
                return Violation("contractVersion missing or not 1.x");
            }

            if (!Uuid(root, "decisionId", out var decisionId))
            {
                return Violation("decisionId missing or not a UUID");
            }

            if (!Uuid(root, "transactionId", out var transactionId))
            {
                return Violation("transactionId missing or not a UUID");
            }

            if (transactionId != expectedTransactionId)
            {
                return Violation("transactionId does not match the request");
            }

            if (!String(root, "policyVersion", out var policyVersion) || policyVersion.Length is 0 or > 128)
            {
                return Violation("policyVersion missing or out of range");
            }

            if (!String(root, "decision", out var decisionText))
            {
                return Violation("decision missing");
            }

            PolicyDecisionValue? decision = decisionText switch
            {
                "APPROVED" => PolicyDecisionValue.Approved,
                "REJECTED" => PolicyDecisionValue.Rejected,
                "REVIEW_REQUIRED" => PolicyDecisionValue.ReviewRequired,
                _ => null,
            };
            if (decision is null)
            {
                // A decision value this client does not understand is never treated as approval.
                return Violation($"unknown decision value");
            }

            if (!root.TryGetProperty("reasonCodes", out var reasons) || reasons.ValueKind != JsonValueKind.Array)
            {
                return Violation("reasonCodes missing or not an array");
            }

            var reasonCodes = new List<string>();
            foreach (var reason in reasons.EnumerateArray())
            {
                if (reason.ValueKind != JsonValueKind.String || !ReasonCodePattern().IsMatch(reason.GetString()!))
                {
                    return Violation("reasonCodes contains an invalid code");
                }

                reasonCodes.Add(reason.GetString()!);
            }

            if (reasonCodes.Distinct(StringComparer.Ordinal).Count() != reasonCodes.Count)
            {
                return Violation("reasonCodes contains duplicates");
            }

            if ((decision == PolicyDecisionValue.Approved) != (reasonCodes.Count == 0))
            {
                return Violation("APPROVED must have no reason codes; other decisions need at least one");
            }

            if (!String(root, "evaluatedAt", out var evaluatedText)
                || !DateTimeOffset.TryParse(evaluatedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var evaluatedAt)
                || !Rfc3339Pattern().IsMatch(evaluatedText))
            {
                return Violation("evaluatedAt missing or not an RFC 3339 date-time");
            }

            return new PolicyEvaluationResult.Decided(new PolicyDecision(
                decisionId, transactionId, policyVersion, decision.Value, reasonCodes, evaluatedAt, contractVersion));
        }
    }

    /// <summary>The <c>code</c> of an RFC 9457 problem body, if there is one.</summary>
    public static string? ProblemCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("code", out var code)
                   && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool String(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString()!;
        return true;
    }

    private static bool Uuid(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return String(root, name, out var text) && Guid.TryParseExact(text, "D", out value);
    }

    private static PolicyEvaluationResult.Failed Violation(string detail) =>
        new(PolicyFailureKind.ContractViolation, detail);

    private static PolicyEvaluationResult.Failed Invalid(string detail) =>
        new(PolicyFailureKind.InvalidResponse, detail);

    [GeneratedRegex(@"^1\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ContractVersionPattern();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonCodePattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339Pattern();
}
