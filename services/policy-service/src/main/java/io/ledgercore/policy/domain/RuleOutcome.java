package io.ledgercore.policy.domain;

/**
 * What a matching rule contributes. There is deliberately no "approve" rule: approval is the
 * absence of any matching restriction, so a rule can only ever make a decision stricter.
 */
public enum RuleOutcome {
  REVIEW_REQUIRED(Decision.REVIEW_REQUIRED),
  REJECTED(Decision.REJECTED);

  private final Decision decision;

  RuleOutcome(Decision decision) {
    this.decision = decision;
  }

  public Decision decision() {
    return decision;
  }
}
