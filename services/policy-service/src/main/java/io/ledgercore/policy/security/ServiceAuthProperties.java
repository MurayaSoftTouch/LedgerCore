package io.ledgercore.policy.security;

import jakarta.validation.constraints.AssertTrue;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.Size;
import org.springframework.boot.context.properties.ConfigurationProperties;
import org.springframework.validation.annotation.Validated;

/**
 * Shared service credentials (ADR-013). Supplied only through the environment; never committed,
 * never logged.
 *
 * @param decisionToken required bearer credential for {@code POST /v1/policy-decisions}; the
 *     service refuses to start without it
 * @param adminToken bearer credential for the management API; when absent the management API is
 *     disabled. Must differ from {@code decisionToken}: evaluation and administration are separate
 *     trust boundaries.
 */
@Validated
@ConfigurationProperties(prefix = "ledgercore.policy.auth")
public record ServiceAuthProperties(
    @NotBlank @Size(min = 32, max = 512) String decisionToken,
    @Size(min = 32, max = 512) String adminToken) {

  public ServiceAuthProperties {
    adminToken = adminToken == null || adminToken.isBlank() ? null : adminToken;
  }

  public boolean managementEnabled() {
    return adminToken != null;
  }

  @AssertTrue(message = "admin-token must differ from decision-token")
  public boolean isAdminTokenDistinct() {
    return adminToken == null || !adminToken.equals(decisionToken);
  }

  @Override
  public String toString() {
    return "ServiceAuthProperties[decisionToken=***, adminToken="
        + (adminToken == null ? "unset" : "***")
        + "]";
  }
}
