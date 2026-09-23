package io.ledgercore.policy;

import static io.ledgercore.policy.support.Requests.STANDARD_RULES;
import static io.ledgercore.policy.support.Requests.decision;
import static org.assertj.core.api.Assertions.assertThat;

import io.ledgercore.policy.support.Expect;
import io.ledgercore.policy.support.PolicyApi.Scoped;
import io.ledgercore.policy.support.PolicyPostgres;
import io.ledgercore.policy.support.PostgresIntegrationTest;
import io.ledgercore.policy.support.Sql;
import java.util.UUID;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

/**
 * Raw SQL as policy_app (the schema owner) that bypasses the service. Every protection here is a
 * trigger or constraint; policy_app has no privilege restrictions (see policy-engine.md).
 */
class DatabaseGuardTests extends PostgresIntegrationTest {

  private Scoped scope;
  private UUID decisionId;

  private void setUp() throws Exception {
    scope = api().activePolicy(STANDARD_RULES);
    var result =
        api().evaluate(decision(UUID.randomUUID(), scope.organizationId(), "ADJUSTMENT", "60000"));
    decisionId = UUID.fromString(Expect.json(result, "$.decisionId"));
  }

  @ParameterizedTest
  @ValueSource(
      strings = {
        "UPDATE policy_decisions SET decision = 'APPROVED', reason_codes = '{}' WHERE id = ?",
        "UPDATE policy_decisions SET evaluated_at = now() WHERE id = ?",
        "DELETE FROM policy_decisions WHERE id = ?",
        "UPDATE policy_decision_matches SET reason_code = 'FORGED' WHERE decision_id = ?",
        "DELETE FROM policy_decision_matches WHERE decision_id = ?"
      })
  void decisionHistoryCannotBeChanged(String sql) throws Exception {
    setUp();

    Sql.assertGuard(Sql.fails(sql, decisionId), "DECISION_IMMUTABLE");

    var detail = api().get("/api/v1/policy-decisions/" + decisionId);
    assertThat((String) Expect.json(detail, "$.decision")).isEqualTo("REJECTED");
  }

  @Test
  void matchesCannotBeAddedAfterTheDecision() throws Exception {
    setUp();

    var error =
        Sql.fails(
            "INSERT INTO policy_decision_matches (decision_id, rule_id, policy_version_id, position, outcome, reason_code)"
                + " SELECT d.id, r.id, r.policy_version_id, r.position, r.outcome, r.reason_code"
                + " FROM policy_decisions d JOIN policy_rules r ON r.policy_version_id = d.policy_version_id"
                + " WHERE d.id = ? AND r.position = 5",
            decisionId);

    Sql.assertGuard(error, "DECISION_IMMUTABLE");
  }

  @ParameterizedTest
  @ValueSource(
      strings = {
        "UPDATE policy_rules SET threshold = 1 WHERE policy_version_id IN (SELECT id FROM policy_versions WHERE policy_id = ?)",
        "DELETE FROM policy_rules WHERE policy_version_id IN (SELECT id FROM policy_versions WHERE policy_id = ?)"
      })
  void activeVersionRulesCannotBeChanged(String sql) throws Exception {
    setUp();

    Sql.assertGuard(Sql.fails(sql, scope.policyId()), "VERSION_IMMUTABLE");
  }

  @Test
  void rulesCannotBeAddedToAnExistingDraft() throws Exception {
    var api = api();
    var org = UUID.randomUUID();
    var policy = api.createPolicy("g-" + org.toString().substring(0, 12), org);
    api.createVersion(policy, "[]");

    var error =
        Sql.fails(
            "INSERT INTO policy_rules (id, policy_version_id, position, rule_type, outcome, reason_code, allowed_currencies)"
                + " SELECT gen_random_uuid(), id, 1, 'CURRENCY_NOT_ALLOWED', 'REJECTED', 'LATE_RULE', ARRAY['KES']"
                + " FROM policy_versions WHERE policy_id = ?",
            policy);

    Sql.assertGuard(error, "VERSION_IMMUTABLE");
  }

  @ParameterizedTest
  @ValueSource(
      strings = {
        "UPDATE policy_versions SET status = 'DRAFT', activated_at = NULL, activated_by = NULL WHERE policy_id = ?|VERSION_INVALID_TRANSITION",
        "UPDATE policy_versions SET version_number = 99 WHERE policy_id = ?|VERSION_IMMUTABLE",
        "UPDATE policy_versions SET activated_at = now() - interval '1 year' WHERE policy_id = ?|VERSION_INVALID_TRANSITION",
        "DELETE FROM policy_versions WHERE policy_id = ?|VERSION_IMMUTABLE",
        "UPDATE policies SET organization_id = NULL WHERE id = ?|POLICY_IMMUTABLE",
        "DELETE FROM policies WHERE id = ?|POLICY_IMMUTABLE"
      })
  void versionsAndPoliciesCannotBeRewritten(String sqlAndCode) throws Exception {
    setUp();
    var parts = sqlAndCode.split("\\|");

    Sql.assertGuard(Sql.fails(parts[0], scope.policyId()), parts[1]);
  }

  @Test
  void retiredVersionCannotBeReactivated() throws Exception {
    setUp();
    Expect.status(
        api().postNoBody("/api/v1/policies/" + scope.policyId() + "/versions/1/retire"), 200);

    var error =
        Sql.fails(
            "UPDATE policy_versions SET status = 'ACTIVE', retired_at = NULL, retired_by = NULL WHERE policy_id = ?",
            scope.policyId());

    Sql.assertGuard(error, "VERSION_INVALID_TRANSITION");
  }

  @Test
  void versionNumbersCannotSkip() throws Exception {
    setUp();

    var error =
        Sql.fails(
            "INSERT INTO policy_versions (id, policy_id, version_number, status, created_at, created_by)"
                + " VALUES (gen_random_uuid(), ?, 5, 'DRAFT', now(), 'sql')",
            scope.policyId());

    Sql.assertGuard(error, "VERSION_NUMBER_NOT_MONOTONIC");
  }

  @Test
  void versionsCannotBeInsertedActive() throws Exception {
    setUp();

    var error =
        Sql.fails(
            "INSERT INTO policy_versions (id, policy_id, version_number, status, created_at, created_by, activated_at, activated_by)"
                + " VALUES (gen_random_uuid(), ?, 2, 'ACTIVE', now(), 'sql', now(), 'sql')",
            scope.policyId());

    Sql.assertGuard(error, "VERSION_INVALID_STATE");
  }

  @Test
  void decisionsCannotBeMadeByARetiredVersion() throws Exception {
    setUp();
    Expect.status(
        api().postNoBody("/api/v1/policies/" + scope.policyId() + "/versions/1/retire"), 200);

    var error =
        Sql.fails(
            "INSERT INTO policy_decisions (id, transaction_id, organization_id, request_fingerprint, policy_id,"
                + " policy_version_id, policy_version_label, decision, reason_codes, transaction_type, currency,"
                + " total_amount, contract_version, evaluated_at)"
                + " SELECT gen_random_uuid(), gen_random_uuid(), ?, repeat('a', 64), policy_id, id, 'x@1',"
                + " 'APPROVED', '{}', 'PAYMENT', 'KES', 1, '1.0.0', now() FROM policy_versions WHERE policy_id = ?",
            scope.organizationId(),
            scope.policyId());

    Sql.assertGuard(error, "VERSION_NOT_ACTIVE");
  }

  @Test
  void nothingCanBeApprovedWithoutAPolicyVersion() {
    var error =
        Sql.fails(
            "INSERT INTO policy_decisions (id, transaction_id, organization_id, request_fingerprint,"
                + " policy_version_label, decision, reason_codes, transaction_type, currency, total_amount,"
                + " contract_version, evaluated_at) VALUES (gen_random_uuid(), gen_random_uuid(),"
                + " gen_random_uuid(), repeat('a', 64), 'none', 'APPROVED', '{}', 'PAYMENT', 'KES', 1, '1.0.0', now())");

    assertThat(error.getSQLState()).isEqualTo("23514");
    assertThat(error.getServerErrorMessage().getConstraint())
        .isEqualTo("ck_policy_decisions_approval_needs_version");
  }

  @ParameterizedTest
  @ValueSource(
      strings = {
        "'AMOUNT_ABOVE', 'REJECTED', 'X_Y', 'KES', NULL, NULL",
        "'AMOUNT_ABOVE', 'REJECTED', 'X_Y', NULL, 10, NULL",
        "'TRANSACTION_TYPE', 'REJECTED', 'X_Y', NULL, NULL, NULL",
        "'CURRENCY_NOT_ALLOWED', 'REJECTED', 'X_Y', NULL, NULL, NULL",
        "'ACCOUNT_CONTEXT', 'REJECTED', 'X_Y', NULL, NULL, NULL",
        "'AMOUNT_ABOVE', 'REJECTED', 'X_Y', 'KES', 10, ARRAY['PAYMENT']",
        "'TRANSACTION_TYPE', 'REJECTED', 'X_Y', NULL, NULL, ARRAY['WIRE']",
        "'SCRIPT', 'REJECTED', 'X_Y', NULL, NULL, NULL",
        "'CURRENCY_NOT_ALLOWED', 'APPROVED', 'X_Y', NULL, NULL, NULL"
      })
  void malformedRuleRowsViolateTheShapeConstraints(String values) throws Exception {
    var api = api();
    var organization = UUID.randomUUID();
    var policy = api.createPolicy("m-" + organization.toString().substring(0, 12), organization);

    try (var c = PolicyPostgres.connect()) {
      c.setAutoCommit(false);
      var version = UUID.randomUUID();
      Sql.execute(
          c,
          "INSERT INTO policy_versions (id, policy_id, version_number, status, created_at, created_by)"
              + " VALUES (?, ?, 1, 'DRAFT', now(), 'sql')",
          version,
          policy);
      var error =
          org.assertj.core.api.Assertions.catchThrowableOfType(
              org.postgresql.util.PSQLException.class,
              () ->
                  Sql.execute(
                      c,
                      "INSERT INTO policy_rules (id, policy_version_id, position, rule_type, outcome, reason_code,"
                          + " currency, threshold, transaction_types) VALUES (gen_random_uuid(), ?, 1, "
                          + values
                          + ")",
                      version));
      assertThat(error.getSQLState()).isEqualTo("23514");
      c.rollback();
    }
  }

  @ParameterizedTest
  @ValueSource(
      strings = {
        "policies",
        "policy_versions",
        "policy_rules",
        "policy_decisions",
        "policy_decision_matches"
      })
  void truncateIsForbidden(String table) {
    assertThat(Sql.fails("TRUNCATE " + table + " CASCADE").getSQLState())
        .isEqualTo(Sql.POLICY_GUARD);
  }
}
