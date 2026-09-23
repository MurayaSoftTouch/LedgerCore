package io.ledgercore.policy.domain;

/**
 * Policy-version lifecycle (ADR-009): {@code DRAFT → ACTIVE → RETIRED}, or {@code DRAFT → RETIRED}
 * to withdraw a version that was never used. {@code RETIRED} is terminal.
 */
public enum VersionStatus {
  DRAFT,
  ACTIVE,
  RETIRED
}
