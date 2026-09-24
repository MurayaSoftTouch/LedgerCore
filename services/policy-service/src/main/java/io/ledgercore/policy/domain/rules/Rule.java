package io.ledgercore.policy.domain.rules;

import io.ledgercore.policy.domain.EvaluationInput;
import io.ledgercore.policy.domain.PolicyDomainException;
import io.ledgercore.policy.domain.ReasonCodes;
import io.ledgercore.policy.domain.RuleOutcome;

/**
 * A structured rule: data interpreted by fixed code. There are no expressions, scripts or SQL. A
 * rule either matches the input (and contributes its outcome and reason code) or it does not.
 */
public sealed interface Rule
    permits AmountAboveRule, TransactionTypeRule, AccountContextRule, CurrencyNotAllowedRule {

  int MAX_RULES_PER_VERSION = 100;

  /** Upper bound on every list inside one rule (types, account ids, currencies). */
  int MAX_LIST_ITEMS = 50;

  /** 1-based, unique within a version. Evaluation and reporting order; never row order. */
  int position();

  RuleOutcome outcome();

  String reasonCode();

  RuleType type();

  boolean matches(EvaluationInput input);

  /** Validation shared by every rule record's compact constructor. */
  static void validateCommon(int position, RuleOutcome outcome, String reasonCode) {
    if (position < 1 || position > MAX_RULES_PER_VERSION) {
      throw PolicyDomainException.invalid(
          "RULE_POSITION_INVALID", "Rule position must be 1-" + MAX_RULES_PER_VERSION + ".");
    }
    if (outcome == null) {
      throw PolicyDomainException.invalid(
          "RULE_OUTCOME_REQUIRED", "Rule outcome must be REVIEW_REQUIRED or REJECTED.");
    }
    ReasonCodes.require(reasonCode);
  }
}
