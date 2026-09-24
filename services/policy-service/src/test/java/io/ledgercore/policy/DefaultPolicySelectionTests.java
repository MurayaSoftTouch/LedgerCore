package io.ledgercore.policy;

import static io.ledgercore.policy.support.Requests.STANDARD_RULES;
import static io.ledgercore.policy.support.Requests.decision;
import static org.assertj.core.api.Assertions.assertThat;

import io.ledgercore.policy.support.Expect;
import io.ledgercore.policy.support.PostgresIntegrationTest;
import java.util.List;
import java.util.UUID;
import org.junit.jupiter.api.MethodOrderer;
import org.junit.jupiter.api.Order;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.TestMethodOrder;

/**
 * The only test class that creates the (single, global) default policy. Ordered: the "no policy at
 * all" case must run before the default exists. Every other test class uses organization-scoped
 * policies.
 */
@TestMethodOrder(MethodOrderer.OrderAnnotation.class)
class DefaultPolicySelectionTests extends PostgresIntegrationTest {

  private static UUID defaultPolicy;

  @Test
  @Order(1)
  void withoutAnyApplicablePolicyTheDecisionIsReviewRequired() throws Exception {
    var result = api().evaluate(decision(UUID.randomUUID(), UUID.randomUUID(), "PAYMENT", "1"));

    Expect.status(result, 200);
    assertThat((String) Expect.json(result, "$.decision")).isEqualTo("REVIEW_REQUIRED");
    assertThat(Expect.<List<String>>json(result, "$.reasonCodes"))
        .containsExactly("NO_APPLICABLE_POLICY");
    assertThat((String) Expect.json(result, "$.policyVersion")).isEqualTo("none");
  }

  @Test
  @Order(2)
  void organizationWithoutItsOwnPolicyUsesTheDefault() throws Exception {
    var api = api();
    defaultPolicy = api.createPolicy("default-limits", null);
    api.activate(defaultPolicy, api.createVersion(defaultPolicy, STANDARD_RULES));

    var result = api.evaluate(decision(UUID.randomUUID(), UUID.randomUUID(), "PAYMENT", "20000"));

    assertThat((String) Expect.json(result, "$.decision")).isEqualTo("REVIEW_REQUIRED");
    assertThat((String) Expect.json(result, "$.policyVersion")).isEqualTo("default-limits@1");
  }

  @Test
  @Order(3)
  void thereIsOnlyOneDefaultPolicy() throws Exception {
    Expect.problem(
        api().post("/api/v1/policies", "{\"key\": \"another-default\", \"name\": \"x\"}"),
        409,
        "DEFAULT_POLICY_EXISTS");
  }

  @Test
  @Order(4)
  void organizationPolicyTakesPrecedenceOverTheDefault() throws Exception {
    var scope =
        api()
            .activePolicy(
                """
                [{"type": "AMOUNT_ABOVE", "currency": "KES", "threshold": "1",
                  "outcome": "REJECTED", "reasonCode": "ORG_LIMIT"}]
                """);

    var result =
        api().evaluate(decision(UUID.randomUUID(), scope.organizationId(), "PAYMENT", "20000"));

    assertThat((String) Expect.json(result, "$.decision")).isEqualTo("REJECTED");
    assertThat((String) Expect.json(result, "$.policyVersion")).isEqualTo(scope.key() + "@1");
  }

  @Test
  @Order(5)
  void inactiveOrganizationPolicyDoesNotFallBackToTheMorePermissiveDefault() throws Exception {
    var api = api();
    var org = UUID.randomUUID();
    var policy = api.createPolicy("paused-" + org.toString().substring(0, 8), org);
    api.activate(policy, api.createVersion(policy, "[]"));
    Expect.status(api.postNoBody("/api/v1/policies/" + policy + "/versions/1/retire"), 200);

    var result = api.evaluate(decision(UUID.randomUUID(), org, "PAYMENT", "1"));

    assertThat((String) Expect.json(result, "$.decision")).isEqualTo("REVIEW_REQUIRED");
    assertThat(Expect.<List<String>>json(result, "$.reasonCodes"))
        .containsExactly("NO_ACTIVE_POLICY_VERSION");
  }
}
