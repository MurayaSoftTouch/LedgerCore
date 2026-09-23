package io.ledgercore.policy.support;

import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.webmvc.test.autoconfigure.AutoConfigureMockMvc;
import org.springframework.test.context.DynamicPropertyRegistry;
import org.springframework.test.context.DynamicPropertySource;
import org.springframework.test.web.servlet.MockMvc;

/** Full application against real PostgreSQL. Subclasses share one cached Spring context. */
@SpringBootTest
@AutoConfigureMockMvc
public abstract class PostgresIntegrationTest {

  @Autowired protected MockMvc mockMvc;

  @DynamicPropertySource
  static void policyDatabase(DynamicPropertyRegistry registry) {
    registry.add("spring.datasource.url", PolicyPostgres::jdbcUrl);
    // As in production: the service runs as policy_runtime, Flyway migrates as policy_app.
    registry.add("spring.datasource.username", () -> "policy_runtime");
    registry.add("spring.datasource.password", () -> PolicyPostgres.POLICY_RUNTIME_PASSWORD);
    registry.add("spring.flyway.user", () -> "policy_app");
    registry.add("spring.flyway.password", () -> PolicyPostgres.POLICY_PASSWORD);
    registry.add("spring.datasource.hikari.maximum-pool-size", () -> "20");
  }

  protected PolicyApi api() {
    return new PolicyApi(mockMvc);
  }
}
