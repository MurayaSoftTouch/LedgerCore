package io.ledgercore.policy;

import static io.ledgercore.policy.support.Requests.STANDARD_RULES;
import static io.ledgercore.policy.support.Requests.decision;
import static org.assertj.core.api.Assertions.assertThat;

import io.ledgercore.policy.support.Expect;
import io.ledgercore.policy.support.PolicyPostgres;
import io.ledgercore.policy.support.PostgresIntegrationTest;
import io.ledgercore.policy.support.Sql;
import java.util.UUID;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

/**
 * The service's database role (policy_runtime) cannot change the schema, bypass or disable the
 * guards, or destroy history. DatabaseGuardTests proves the guards hold even for the owner.
 */
class RuntimeRoleTests extends PostgresIntegrationTest {

  private static final String INSUFFICIENT_PRIVILEGE = "42501";

  @Test
  void serviceConnectsAsTheRuntimeRole() throws Exception {
    // A decision written through the API proves the runtime role has what the service needs...
    var scope = api().activePolicy(STANDARD_RULES);
    Expect.status(
        api().evaluate(decision(UUID.randomUUID(), scope.organizationId(), "PAYMENT", "1")), 200);

    // ...and the pool really is policy_runtime, not the owner.
    try (var c = PolicyPostgres.connectRuntime();
        var rs = c.createStatement().executeQuery("SELECT current_user")) {
      rs.next();
      assertThat(rs.getString(1)).isEqualTo("policy_runtime");
    }
    assertThat(
            Sql.count(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = 'policy' AND usename = 'policy_app'"
                    + " AND application_name <> 'psql' AND state IS NOT NULL AND backend_type = 'client backend'"
                    + " AND pid <> pg_backend_pid()"))
        .as("no pooled connection runs as the schema owner")
        .isZero();
  }

  @ParameterizedTest
  @ValueSource(
      strings = {
        "ALTER TABLE policy_decisions DISABLE TRIGGER policy_decisions_immutable",
        "ALTER TABLE policy_rules DISABLE TRIGGER ALL",
        "DROP TRIGGER policy_versions_before_update ON policy_versions",
        "CREATE OR REPLACE FUNCTION policy_forbid_mutation() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$",
        "DROP TABLE policy_decision_matches",
        "ALTER TABLE policies ADD COLUMN backdoor text",
        "CREATE TABLE shadow_decisions (id uuid)",
        "TRUNCATE policy_decisions CASCADE",
        "DELETE FROM policy_decisions",
        "DELETE FROM policy_versions",
        "UPDATE policy_rules SET threshold = 0",
        "UPDATE policy_decisions SET decision = 'APPROVED'",
        "INSERT INTO flyway_schema_history (installed_rank, description, type, script, installed_by, execution_time, success) VALUES (99, 'x', 'SQL', 'x', 'x', 0, true)"
      })
  void runtimeRoleCannotAlterSchemaDisableGuardsOrDestroyHistory(String sql) {
    assertThat(Sql.failsAsRuntime(sql).getSQLState()).isEqualTo(INSUFFICIENT_PRIVILEGE);
  }

  @Test
  void runtimeRoleStillHitsTheGuardsWhereItHasPrivileges() throws Exception {
    // UPDATE on policies is granted only to allow the per-policy row lock; the trigger forbids it.
    // Row triggers need a row: target this test's own policy, so the result doesn't depend on
    // which tests ran first (it silently updated nothing when run alone).
    var scope = api().activePolicy(STANDARD_RULES);
    Sql.assertGuard(
        Sql.failsAsRuntime(
            "UPDATE policies SET name = 'renamed' WHERE organization_id = ?",
            scope.organizationId()),
        "POLICY_IMMUTABLE");
  }

  @Test
  void runtimeRoleCannotReadTheLedgerDatabase() {
    var error =
        org.assertj.core.api.Assertions.catchThrowable(
            () ->
                java.sql.DriverManager.getConnection(
                        PolicyPostgres.jdbcUrl().replace("/policy", "/ledger"),
                        "policy_runtime",
                        PolicyPostgres.POLICY_RUNTIME_PASSWORD)
                    .close());

    assertThat(error).isInstanceOf(java.sql.SQLException.class);
    assertThat(((java.sql.SQLException) error).getSQLState()).isEqualTo(INSUFFICIENT_PRIVILEGE);
  }
}
