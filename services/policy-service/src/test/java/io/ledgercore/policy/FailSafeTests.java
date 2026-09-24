package io.ledgercore.policy;

import static io.ledgercore.policy.support.Requests.STANDARD_RULES;
import static io.ledgercore.policy.support.Requests.decision;
import static org.assertj.core.api.Assertions.assertThat;
import static org.mockito.ArgumentMatchers.any;
import static org.mockito.Mockito.doThrow;
import static org.mockito.Mockito.reset;

import io.ledgercore.policy.domain.PolicyDomainException;
import io.ledgercore.policy.persistence.DecisionRepository;
import io.ledgercore.policy.persistence.PolicyVersionRepository;
import io.ledgercore.policy.support.Expect;
import io.ledgercore.policy.support.PolicyApi.Scoped;
import io.ledgercore.policy.support.PostgresIntegrationTest;
import io.ledgercore.policy.support.Sql;
import java.util.UUID;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.dao.DataAccessResourceFailureException;
import org.springframework.test.context.bean.override.mockito.MockitoSpyBean;
import org.springframework.test.web.servlet.MvcResult;

/**
 * An evaluation that cannot be trusted must never produce a decision: 503 with Retry-After, and
 * nothing recorded, so the ledger fails closed and can retry (ADR-005).
 */
class FailSafeTests extends PostgresIntegrationTest {

  @MockitoSpyBean private PolicyVersionRepository versions;
  @MockitoSpyBean private DecisionRepository decisions;

  private Scoped scope;

  @BeforeEach
  void policy() throws Exception {
    scope = api().activePolicy(STANDARD_RULES);
  }

  @AfterEach
  void restore() {
    reset(versions, decisions);
  }

  private void assertUnavailableAndNothingRecorded(UUID transaction, MvcResult result)
      throws Exception {
    Expect.problem(result, 503, "POLICY_EVALUATION_UNAVAILABLE");
    assertThat(result.getResponse().getHeader("Retry-After")).isEqualTo("1");
    assertThat(result.getResponse().getContentAsString())
        .doesNotContain("Exception", "database down", "bug");
    assertThat(
            Sql.count(
                "SELECT count(*) FROM policy_decisions WHERE transaction_id = ?", transaction))
        .isZero();
  }

  @Test
  void policyStoreFailureIsUnavailableNotADecision() throws Exception {
    doThrow(new DataAccessResourceFailureException("database down"))
        .when(versions)
        .findActiveForEvaluation(any());
    var transaction = UUID.randomUUID();

    assertUnavailableAndNothingRecorded(
        transaction, api().evaluate(decision(transaction, scope.organizationId(), "PAYMENT", "1")));
  }

  @Test
  void internalFailureIsUnavailableNotADecision() throws Exception {
    doThrow(new IllegalStateException("bug")).when(versions).findActiveForEvaluation(any());
    var transaction = UUID.randomUUID();

    assertUnavailableAndNothingRecorded(
        transaction, api().evaluate(decision(transaction, scope.organizationId(), "PAYMENT", "1")));
  }

  @Test
  void invalidStoredRuleConfigurationIsUnavailableNotABadRequest() throws Exception {
    // What rebuilding a corrupted rule row raises: a domain validation error. On the evaluation
    // path
    // it means "cannot evaluate", never "your request is invalid" and never APPROVED.
    doThrow(PolicyDomainException.invalid("RULE_THRESHOLD_INVALID", "corrupt"))
        .when(versions)
        .findActiveForEvaluation(any());
    var transaction = UUID.randomUUID();

    assertUnavailableAndNothingRecorded(
        transaction, api().evaluate(decision(transaction, scope.organizationId(), "PAYMENT", "1")));
  }

  @Test
  void failureWhileRecordingTheDecisionLeavesNoPartialRecord() throws Exception {
    doThrow(new DataAccessResourceFailureException("database down"))
        .when(decisions)
        .insertIfAbsent(any());
    var transaction = UUID.randomUUID();

    assertUnavailableAndNothingRecorded(
        transaction,
        api().evaluate(decision(transaction, scope.organizationId(), "ADJUSTMENT", "60000")));
  }

  @Test
  void serviceRecoversOnceTheFailureClears() throws Exception {
    var transaction = UUID.randomUUID();
    doThrow(new DataAccessResourceFailureException("database down"))
        .when(versions)
        .findActiveForEvaluation(any());
    Expect.status(
        api().evaluate(decision(transaction, scope.organizationId(), "PAYMENT", "1")), 503);

    reset(versions);
    var retry = api().evaluate(decision(transaction, scope.organizationId(), "PAYMENT", "1"));

    Expect.status(retry, 200);
    assertThat((String) Expect.json(retry, "$.decision")).isEqualTo("APPROVED");
  }
}
