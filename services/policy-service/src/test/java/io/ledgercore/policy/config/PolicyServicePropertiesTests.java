package io.ledgercore.policy.config;

import static org.assertj.core.api.Assertions.assertThat;

import org.junit.jupiter.api.Test;
import org.springframework.boot.autoconfigure.AutoConfigurations;
import org.springframework.boot.context.properties.EnableConfigurationProperties;
import org.springframework.boot.test.context.runner.ApplicationContextRunner;
import org.springframework.boot.validation.autoconfigure.ValidationAutoConfiguration;
import org.springframework.context.annotation.Configuration;

class PolicyServicePropertiesTests {

  private final ApplicationContextRunner runner =
      new ApplicationContextRunner()
          .withConfiguration(AutoConfigurations.of(ValidationAutoConfiguration.class))
          .withUserConfiguration(PropertiesConfig.class);

  @Test
  void bindsValidConfiguration() {
    runner
        .withPropertyValues(
            "ledgercore.policy.contract-version=1.0.0", "ledgercore.policy.environment=test")
        .run(
            context -> {
              assertThat(context).hasNotFailed();
              assertThat(context.getBean(PolicyServiceProperties.class).contractVersion())
                  .isEqualTo("1.0.0");
            });
  }

  @Test
  void rejectsMissingContractVersion() {
    runner
        .withPropertyValues("ledgercore.policy.environment=test")
        .run(context -> assertThat(context).hasFailed());
  }

  @Test
  void rejectsMalformedContractVersion() {
    runner
        .withPropertyValues(
            "ledgercore.policy.contract-version=v1", "ledgercore.policy.environment=test")
        .run(context -> assertThat(context).hasFailed());
  }

  @Configuration(proxyBeanMethods = false)
  @EnableConfigurationProperties(PolicyServiceProperties.class)
  static class PropertiesConfig {}
}
