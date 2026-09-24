package io.ledgercore.policy.domain;

/**
 * Contract v1 decision values, ordered by restrictiveness. When several rules match, the most
 * restrictive wins: {@code REJECTED > REVIEW_REQUIRED > APPROVED} (ADR-010).
 */
public enum Decision {
  APPROVED(0),
  REVIEW_REQUIRED(1),
  REJECTED(2);

  private final int severity;

  Decision(int severity) {
    this.severity = severity;
  }

  public int severity() {
    return severity;
  }

  public Decision mostRestrictive(Decision other) {
    return other.severity > severity ? other : this;
  }
}
