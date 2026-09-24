package io.ledgercore.policy.domain.rules;

import io.ledgercore.policy.domain.EvaluationInput;
import io.ledgercore.policy.domain.PolicyDomainException;
import io.ledgercore.policy.domain.RuleOutcome;
import java.math.BigDecimal;
import java.util.regex.Pattern;

/**
 * Matches when the journal is in {@code currency} and its total is strictly greater than {@code
 * threshold}. Amounts in other currencies never match: there is no FX conversion.
 */
public record AmountAboveRule(
    int position, RuleOutcome outcome, String reasonCode, String currency, BigDecimal threshold)
    implements Rule {

  static final Pattern CURRENCY = Pattern.compile("^[A-Z]{3}$");

  /** Same bound as the contract's totalAmount and the ledger's numeric(22,4). */
  static final BigDecimal MAX = new BigDecimal("999999999999999999.9999");

  public AmountAboveRule {
    Rule.validateCommon(position, outcome, reasonCode);
    if (currency == null || !CURRENCY.matcher(currency).matches()) {
      throw PolicyDomainException.invalid(
          "RULE_CURRENCY_INVALID", "AMOUNT_ABOVE requires a 3-letter currency.");
    }
    if (threshold == null
        || threshold.signum() < 0
        || threshold.compareTo(MAX) > 0
        || threshold.stripTrailingZeros().scale() > 4) {
      throw PolicyDomainException.invalid(
          "RULE_THRESHOLD_INVALID",
          "AMOUNT_ABOVE requires a threshold between 0 and " + MAX + " with at most 4 decimals.");
    }
  }

  @Override
  public RuleType type() {
    return RuleType.AMOUNT_ABOVE;
  }

  @Override
  public boolean matches(EvaluationInput input) {
    return currency.equals(input.currency()) && input.totalAmount().compareTo(threshold) > 0;
  }
}
