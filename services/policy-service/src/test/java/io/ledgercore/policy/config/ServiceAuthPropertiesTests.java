package io.ledgercore.policy.config;

import static org.assertj.core.api.Assertions.assertThat;

import io.ledgercore.policy.security.ServiceAuthProperties;
import org.junit.jupiter.api.Test;
import org.springframework.boot.autoconfigure.AutoConfigurations;
import org.springframework.boot.context.properties.EnableConfigurationProperties;
import org.springframework.boot.test.context.runner.ApplicationContextRunner;
import org.springframework.boot.validation.autoconfigure.ValidationAutoConfiguration;
import org.springframework.context.annotation.Configuration;

class ServiceAuthPropertiesTests {

  private static final String DECISION = "d".repeat(40);
  private static final String ADMIN = "a".repeat(40);

  private final ApplicationContextRunner runner =
      new ApplicationContextRunner()
          .withConfiguration(AutoConfigurations.of(ValidationAutoConfiguration.class))
          .withUserConfiguration(Config.class);

  @Test
  void bindsDistinctCredentials() {
    runner
        .withPropertyValues(
            "ledgercore.policy.auth.decision-token=" + DECISION,
            "ledgercore.policy.auth.admin-token=" + ADMIN)
        .run(
            context -> {
              assertThat(context).hasNotFailed();
              var properties = context.getBean(ServiceAuthProperties.class);
              assertThat(properties.managementEnabled()).isTrue();
              assertThat(properties.toString()).doesNotContain(DECISION, ADMIN);
            });
  }

  @Test
  void managementIsDisabledWithoutAdminCredential() {
    runner
        .withPropertyValues("ledgercore.policy.auth.decision-token=" + DECISION)
        .run(
            context ->
                assertThat(context.getBean(ServiceAuthProperties.class).managementEnabled())
                    .isFalse());
  }

  @Test
  void refusesToStartWithoutDecisionCredential() {
    runner.run(context -> assertThat(context).hasFailed());
  }

  @Test
  void refusesShortCredentials() {
    runner
        .withPropertyValues("ledgercore.policy.auth.decision-token=short")
        .run(context -> assertThat(context).hasFailed());
  }

  @Test
  void refusesIdenticalCredentials() {
    runner
        .withPropertyValues(
            "ledgercore.policy.auth.decision-token=" + DECISION,
            "ledgercore.policy.auth.admin-token=" + DECISION)
        .run(context -> assertThat(context).hasFailed());
  }

  @Configuration(proxyBeanMethods = false)
  @EnableConfigurationProperties(ServiceAuthProperties.class)
  static class Config {}
}
