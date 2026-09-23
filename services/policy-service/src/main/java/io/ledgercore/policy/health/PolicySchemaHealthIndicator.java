package io.ledgercore.policy.health;

import org.springframework.boot.health.contributor.Health;
import org.springframework.boot.health.contributor.HealthIndicator;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Component;

/**
 * Readiness: the migrated schema is present and usable by the runtime role. Touches every table the
 * evaluation path needs without reading rows. Part of the readiness group with {@code db}.
 */
@Component("policySchema")
public class PolicySchemaHealthIndicator implements HealthIndicator {

  private final JdbcClient jdbc;

  public PolicySchemaHealthIndicator(JdbcClient jdbc) {
    this.jdbc = jdbc;
  }

  @Override
  public Health health() {
    try {
      jdbc.sql(
              "SELECT 1 FROM policies, policy_versions, policy_rules, policy_decisions,"
                  + " policy_decision_matches LIMIT 0")
          .query()
          .listOfRows();
      return Health.up().build();
    } catch (RuntimeException e) {
      return Health.down().withDetail("reason", "policy schema unavailable").build();
    }
  }
}
