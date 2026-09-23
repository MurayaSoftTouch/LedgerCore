package io.ledgercore.policy.config;

import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.Pattern;
import org.springframework.boot.context.properties.ConfigurationProperties;
import org.springframework.validation.annotation.Validated;

/**
 * Startup-validated configuration. The application refuses to start if any constraint fails, so a
 * misconfigured instance can never serve policy decisions.
 *
 * @param contractVersion the ledger-policy contract version this service implements (see
 *     contracts/openapi/policy-decision.v1.yaml)
 * @param environment deployment environment name, surfaced in logs and /actuator/info
 */
@Validated
@ConfigurationProperties(prefix = "ledgercore.policy")
public record PolicyServiceProperties(
    @NotBlank @Pattern(regexp = "^\\d+\\.\\d+\\.\\d+$") String contractVersion,
    @NotBlank String environment) {}
