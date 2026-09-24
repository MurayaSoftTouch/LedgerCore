using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LedgerCore.Ledger.Api.Integration.Policy;
using LedgerCore.Ledger.Api.Tests.Infrastructure;
using static LedgerCore.Ledger.Api.Tests.Policy.PolicyClientHarness;

namespace LedgerCore.Ledger.Api.Tests.Policy;

/// <summary>
/// Consumer side of contract v1 (1.1.0): what the ledger sends must validate against the
/// repository's request schema, and what it accepts is exactly what the response schema allows.
/// Anything it does not understand fails closed.
/// </summary>
public sealed class PolicyContractConsumerTests
{
    private static PolicyEvaluationResult Parse(string body) => PolicyDecisionClient.Map(HttpStatusCode.OK, body, TransactionId);

    private static PolicyEvaluationResult.Failed Violation(string body)
    {
        var failed = Assert.IsType<PolicyEvaluationResult.Failed>(Parse(body));
        Assert.Equal(PolicyFailureKind.ContractViolation, failed.Kind);
        return failed;
    }

    private static string WithField(string body, string field, JsonNode? value)
    {
        var node = JsonNode.Parse(body)!.AsObject();
        if (value is null)
        {
            node.Remove(field);
        }
        else
        {
            node[field] = value;
        }

        return node.ToJsonString();
    }

    private static string Approved => ExampleResponse("response.approved.valid.json");

    [Fact]
    public void SerializedRequestValidatesAgainstTheContractSchema()
    {
        var body = PolicyDecisionClient.Serialize(Request(), "1.1.0");

        Assert.True(Contracts.IsValidRequest(body), body);
    }

    [Fact]
    public void AmountsStayDecimalStrings()
    {
        using var document = JsonDocument.Parse(PolicyDecisionClient.Serialize(Request("999999999999999999.99"), "1.1.0"));
        var amount = document.RootElement.GetProperty("totalAmount");

        Assert.Equal(JsonValueKind.String, amount.ValueKind);
        Assert.Equal("999999999999999999.99", amount.GetString());
        Assert.Equal("2026-09-23T09:15:00.000Z", document.RootElement.GetProperty("requestedAt").GetString());
    }

    public static TheoryData<string, string> ValidExamples => new()
    {
        { "response.approved.valid.json", nameof(PolicyDecisionValue.Approved) },
        { "response.review-required.valid.json", nameof(PolicyDecisionValue.ReviewRequired) },
        { "response.no-policy.valid.json", nameof(PolicyDecisionValue.ReviewRequired) },
    };

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void EveryValidResponseExampleIsUnderstood(string example, string expectedDecision)
    {
        var expected = Enum.Parse<PolicyDecisionValue>(expectedDecision);
        var body = ExampleResponse(example);
        Assert.True(Contracts.IsValidResponse(body));

        var decision = Assert.IsType<PolicyEvaluationResult.Decided>(Parse(body)).Decision;

        Assert.Equal(expected, decision.Decision);
        Assert.Equal(TransactionId, decision.TransactionId);
    }

    [Fact]
    public void EveryValidResponseExampleIsCovered() =>
        Assert.Equal(
            ValidExamples.Select(row => (string)row[0]).Order(StringComparer.Ordinal),
            Contracts.Examples("response.", ".valid.json"));

    [Fact]
    public void NoPolicyVersionIsAcceptedAsReviewNotApproval()
    {
        var decision = Assert.IsType<PolicyEvaluationResult.Decided>(Parse(ExampleResponse("response.no-policy.valid.json"))).Decision;

        Assert.Equal("none", decision.PolicyVersion);
        Assert.Equal(PolicyDecisionValue.ReviewRequired, decision.Decision);
        Assert.Equal(["NO_APPLICABLE_POLICY"], decision.ReasonCodes);
    }

    [Theory]
    [InlineData("response.rejected-without-reason.invalid.json")]
    public void InvalidResponseExamplesFailClosed(string example)
    {
        var body = ExampleResponse(example);
        Assert.False(Contracts.IsValidResponse(body));

        Violation(body);
    }

    [Theory]
    [InlineData("ESCALATED")]
    [InlineData("approved")]
    [InlineData("")]
    public void UnknownDecisionValuesFailClosed(string decision) =>
        Violation(WithField(Approved, "decision", decision));

    [Theory]
    [InlineData("contractVersion")]
    [InlineData("decisionId")]
    [InlineData("transactionId")]
    [InlineData("policyVersion")]
    [InlineData("decision")]
    [InlineData("reasonCodes")]
    [InlineData("evaluatedAt")]
    public void EveryRequiredFieldIsRequired(string field)
    {
        var body = WithField(Approved, field, null);
        Assert.False(Contracts.IsValidResponse(body));

        Violation(body);
    }

    [Theory]
    [InlineData("decision", 1)]
    [InlineData("decisionId", "not-a-uuid")]
    [InlineData("evaluatedAt", "yesterday")]
    [InlineData("evaluatedAt", "2026-09-23 09:15:00")]
    [InlineData("contractVersion", "2.0.0")]
    [InlineData("policyVersion", "")]
    public void WrongTypesAndFormatsFailClosed(string field, object value) =>
        Violation(WithField(Approved, field, JsonValue.Create(value)));

    [Fact]
    public void ApprovalWithReasonCodesIsAViolation() =>
        Violation(WithField(Approved, "reasonCodes", new JsonArray("SOMETHING")));

    [Fact]
    public void ResponseForAnotherTransactionIsAViolation() =>
        Violation(WithField(Approved, "transactionId", Guid.NewGuid().ToString()));

    [Fact]
    public void UnknownResponseFieldsAreIgnoredAndCannotChangeTheOutcome()
    {
        var withExtras = WithField(WithField(Approved, "override", "APPROVED_FOREVER"), "decisionHint", "REJECTED");

        var plain = Assert.IsType<PolicyEvaluationResult.Decided>(Parse(Approved)).Decision;
        var extended = Assert.IsType<PolicyEvaluationResult.Decided>(Parse(withExtras)).Decision;

        Assert.Equal(plain with { ReasonCodes = [] }, extended with { ReasonCodes = [] });
        Assert.Equal(plain.Decision, extended.Decision);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.9.3")]
    public void AnyOneDotXContractVersionIsAccepted(string version) =>
        Assert.IsType<PolicyEvaluationResult.Decided>(Parse(WithField(Approved, "contractVersion", version)));

    [Fact]
    public void TimestampsWithOffsetsParse()
    {
        var decision = Assert.IsType<PolicyEvaluationResult.Decided>(
            Parse(WithField(Approved, "evaluatedAt", "2026-09-23T12:15:00.120+03:00"))).Decision;

        Assert.Equal(new DateTimeOffset(2026, 9, 23, 9, 15, 0, 120, TimeSpan.Zero), decision.EvaluatedAt.ToUniversalTime());
    }
}
