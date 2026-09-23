package io.ledgercore.policy;

import static io.ledgercore.policy.support.Requests.STANDARD_RULES;
import static io.ledgercore.policy.support.Requests.decision;
import static org.assertj.core.api.Assertions.assertThat;

import io.ledgercore.policy.support.Expect;
import io.ledgercore.policy.support.PolicyApi.Scoped;
import io.ledgercore.policy.support.PostgresIntegrationTest;
import java.util.List;
import java.util.UUID;
import org.junit.jupiter.api.Test;
import org.springframework.test.web.servlet.MvcResult;

class PolicyDecisionApiTests extends PostgresIntegrationTest {

  private MvcResult evaluate(Scoped scope, String type, String amount) throws Exception {
    return api().evaluate(decision(UUID.randomUUID(), scope.organizationId(), type, amount));
  }

  @Test
  void approvesWhenNoRuleMatches() throws Exception {
    var scope = api().activePolicy(STANDARD_RULES);

    var result = evaluate(scope, "PAYMENT", "9999.99");

    Expect.status(result, 200);
    assertThat((String) Expect.json(result, "$.decision")).isEqualTo("APPROVED");
    assertThat(Expect.<List<String>>json(result, "$.reasonCodes")).isEmpty();
    assertThat((String) Expect.json(result, "$.policyVersion")).isEqualTo(scope.key() + "@1");
    assertThat((String) Expect.json(result, "$.contractVersion")).isEqualTo("1.1.0");
    assertThat(result.getResponse().getHeader("X-Correlation-Id")).isEqualTo("test-correlation");
  }

  @Test
  void requiresReviewAboveReviewThreshold() throws Exception {
    var result = evaluate(api().activePolicy(STANDARD_RULES), "PAYMENT", "10000.01");

    assertThat((String) Expect.json(result, "$.decision")).isEqualTo("REVIEW_REQUIRED");
    assertThat(Expect.<List<String>>json(result, "$.reasonCodes"))
        .containsExactly("AMOUNT_EXCEEDS_REVIEW_THRESHOLD");
  }

  @Test
  void rejectsAboveHardLimitAndReportsEveryMatchedRule() throws Exception {
    var result = evaluate(api().activePolicy(STANDARD_RULES), "ADJUSTMENT", "50000.01");

    assertThat((String) Expect.json(result, "$.decision")).isEqualTo("REJECTED");
    assertThat(Expect.<List<String>>json(result, "$.reasonCodes"))
        .containsExactly(
            "AMOUNT_EXCEEDS_HARD_LIMIT",
            "AMOUNT_EXCEEDS_REVIEW_THRESHOLD",
            "TRANSACTION_TYPE_REQUIRES_REVIEW");
  }

  @Test
  void transactionTypeRuleEscalatesToReview() throws Exception {
    var result = evaluate(api().activePolicy(STANDARD_RULES), "ADJUSTMENT", "1.00");

    assertThat((String) Expect.json(result, "$.decision")).isEqualTo("REVIEW_REQUIRED");
    assertThat(Expect.<List<String>>json(result, "$.reasonCodes"))
        .containsExactly("TRANSACTION_TYPE_REQUIRES_REVIEW");
  }

  @Test
  void accountContextRuleRejects() throws Exception {
    var scope = api().activePolicy(STANDARD_RULES);

    var result =
        api()
            .evaluate(
                decision(
                    UUID.randomUUID(),
                    scope.organizationId(),
                    "TRANSFER",
                    "KES",
                    "5",
                    "EQUITY",
                    "ASSET"));

    assertThat((String) Expect.json(result, "$.decision")).isEqualTo("REJECTED");
    assertThat(Expect.<List<String>>json(result, "$.reasonCodes"))
        .containsExactly("ACCOUNT_CONTEXT_RESTRICTED");
  }

  @Test
  void currencyRuleRejects() throws Exception {
    var scope = api().activePolicy(STANDARD_RULES);

    var result =
        api()
            .evaluate(
                decision(
                    UUID.randomUUID(),
                    scope.organizationId(),
                    "PAYMENT",
                    "EUR",
                    "5",
                    "EXPENSE",
                    "ASSET"));

    assertThat((String) Expect.json(result, "$.decision")).isEqualTo("REJECTED");
    assertThat(Expect.<List<String>>json(result, "$.reasonCodes"))
        .containsExactly("CURRENCY_NOT_ALLOWED");
  }

  @Test
  void unknownTransactionTypeFromALaterMinorVersionRequiresReview() throws Exception {
    var result = evaluate(api().activePolicy(STANDARD_RULES), "WIRE", "5");

    Expect.status(result, 200);
    assertThat((String) Expect.json(result, "$.decision")).isEqualTo("REVIEW_REQUIRED");
    assertThat(Expect.<List<String>>json(result, "$.reasonCodes"))
        .containsExactly("TRANSACTION_TYPE_UNSUPPORTED");
  }

  @Test
  void organizationPolicyWithoutActiveVersionRequiresReview() throws Exception {
    var api = api();
    var org = UUID.randomUUID();
    var policy = api.createPolicy("inactive-" + org.toString().substring(0, 8), org);
    api.createVersion(policy, "[]");

    var result = api.evaluate(decision(UUID.randomUUID(), org, "PAYMENT", "1"));

    assertThat((String) Expect.json(result, "$.decision")).isEqualTo("REVIEW_REQUIRED");
    assertThat(Expect.<List<String>>json(result, "$.reasonCodes"))
        .containsExactly("NO_ACTIVE_POLICY_VERSION");
    assertThat((String) Expect.json(result, "$.policyVersion")).isEqualTo("none");
  }

  @Test
  void decisionDetailExplainsTheResult() throws Exception {
    var scope = api().activePolicy(STANDARD_RULES);
    var result = evaluate(scope, "ADJUSTMENT", "60000");
    String decisionId = Expect.json(result, "$.decisionId");

    var detail = api().get("/api/v1/policy-decisions/" + decisionId);

    Expect.status(detail, 200);
    assertThat((String) Expect.json(detail, "$.policyId")).isEqualTo(scope.policyId().toString());
    assertThat(Expect.<List<Integer>>json(detail, "$.matchedRules[*].position"))
        .containsExactly(1, 2, 3);
    assertThat((String) Expect.json(detail, "$.evaluatedInputs.totalAmount"))
        .isEqualTo("60000.0000");
    assertThat((String) Expect.json(detail, "$.correlationId")).isEqualTo("test-correlation");
    assertThat((String) Expect.json(detail, "$.evaluatedAt"))
        .isEqualTo(Expect.json(result, "$.evaluatedAt"));
  }

  @Test
  void historicalDecisionStaysBoundToTheVersionThatMadeIt() throws Exception {
    var api = api();
    var scope =
        api.activePolicy(
            """
            [{"type": "AMOUNT_ABOVE", "currency": "KES", "threshold": "10000",
              "outcome": "REVIEW_REQUIRED", "reasonCode": "AMOUNT_EXCEEDS_REVIEW_THRESHOLD"}]
            """);
    var transaction = UUID.randomUUID();
    var request = decision(transaction, scope.organizationId(), "PAYMENT", "20000");
    var first = api.evaluate(request);
    assertThat((String) Expect.json(first, "$.decision")).isEqualTo("REVIEW_REQUIRED");

    // v2 raises the threshold; new transactions are approved under it.
    var v2 =
        api.createVersion(
            scope.policyId(),
            """
            [{"type": "AMOUNT_ABOVE", "currency": "KES", "threshold": "50000",
              "outcome": "REVIEW_REQUIRED", "reasonCode": "AMOUNT_EXCEEDS_REVIEW_THRESHOLD"}]
            """);
    api.activate(scope.policyId(), v2);
    var fresh =
        api.evaluate(decision(UUID.randomUUID(), scope.organizationId(), "PAYMENT", "20000"));
    assertThat((String) Expect.json(fresh, "$.decision")).isEqualTo("APPROVED");
    assertThat((String) Expect.json(fresh, "$.policyVersion")).isEqualTo(scope.key() + "@2");

    // The original transaction's decision is unchanged, still v1.
    var replay = api.evaluate(request);
    assertThat(replay.getResponse().getContentAsString())
        .isEqualTo(first.getResponse().getContentAsString());
    assertThat((String) Expect.json(replay, "$.policyVersion")).isEqualTo(scope.key() + "@1");
    assertThat(replay.getResponse().getHeader("X-Decision-Replayed")).isEqualTo("true");
    String decisionId = Expect.json(first, "$.decisionId");
    var detail = api.get("/api/v1/policy-decisions/" + decisionId);
    assertThat((String) Expect.json(detail, "$.policyVersion")).isEqualTo(scope.key() + "@1");
    assertThat((String) Expect.json(detail, "$.decision")).isEqualTo("REVIEW_REQUIRED");
  }

  @Test
  void identicalRetryReturnsTheSameDecisionEvenWithNewTimestampAndPrincipal() throws Exception {
    var scope = api().activePolicy(STANDARD_RULES);
    var transaction = UUID.randomUUID();
    var first = api().evaluate(decision(transaction, scope.organizationId(), "PAYMENT", "100.50"));

    var retry =
        api()
            .evaluate(
                decision(transaction, scope.organizationId(), "PAYMENT", "100.5")
                    .replace("2026-09-23T09:15:00Z", "2026-09-23T09:16:30Z")
                    .replace("user-4821", "service-retry")
                    .replace("\"1.0.0\"", "\"1.0.1\""));

    Expect.status(retry, 200);
    assertThat((String) Expect.json(retry, "$.decisionId"))
        .isEqualTo(Expect.json(first, "$.decisionId"));
    assertThat((String) Expect.json(retry, "$.evaluatedAt"))
        .isEqualTo(Expect.json(first, "$.evaluatedAt"));
    assertThat(first.getResponse().getHeader("X-Decision-Replayed")).isEqualTo("false");
    assertThat(retry.getResponse().getHeader("X-Decision-Replayed")).isEqualTo("true");
  }

  @Test
  void conflictingDuplicateIsRejectedAndOriginalStands() throws Exception {
    var scope = api().activePolicy(STANDARD_RULES);
    var transaction = UUID.randomUUID();
    var first = api().evaluate(decision(transaction, scope.organizationId(), "PAYMENT", "100"));

    Expect.problem(
        api().evaluate(decision(transaction, scope.organizationId(), "PAYMENT", "60000")),
        409,
        "IDEMPOTENCY_CONFLICT");

    var replay = api().evaluate(decision(transaction, scope.organizationId(), "PAYMENT", "100"));
    assertThat((String) Expect.json(replay, "$.decision")).isEqualTo("APPROVED");
    assertThat((String) Expect.json(replay, "$.decisionId"))
        .isEqualTo(Expect.json(first, "$.decisionId"));
  }

  @Test
  void unknownDecisionIsNotFound() throws Exception {
    Expect.problem(
        api().get("/api/v1/policy-decisions/" + UUID.randomUUID()), 404, "DECISION_NOT_FOUND");
  }
}
