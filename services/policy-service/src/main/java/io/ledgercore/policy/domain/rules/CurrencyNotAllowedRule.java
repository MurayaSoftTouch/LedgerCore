package io.ledgercore.policy.domain.rules;

import io.ledgercore.policy.domain.EvaluationInput;
import io.ledgercore.policy.domain.PolicyDomainException;
import io.ledgercore.policy.domain.RuleOutcome;
import java.util.Collections;
import java.util.Set;
import java.util.TreeSet;

/** Matches when the request's currency is <em>not</em> in {@code allowedCurrencies}. */
public record CurrencyNotAllowedRule(
    int position, RuleOutcome outcome, String reasonCode, Set<String> allowedCurrencies)
    implements Rule {

  public CurrencyNotAllowedRule {
    Rule.validateCommon(position, outcome, reasonCode);
    if (allowedCurrencies == null
        || allowedCurrencies.isEmpty()
        || !allowedCurrencies.stream()
            .allMatch(c -> c != null && AmountAboveRule.CURRENCY.matcher(c).matches())) {
      throw PolicyDomainException.invalid(
          "RULE_CURRENCIES_INVALID",
          "CURRENCY_NOT_ALLOWED requires one or more 3-letter currency codes.");
    }
    allowedCurrencies = Collections.unmodifiableSet(new TreeSet<>(allowedCurrencies));
  }

  @Override
  public RuleType type() {
    return RuleType.CURRENCY_NOT_ALLOWED;
  }

  @Override
  public boolean matches(EvaluationInput input) {
    return !allowedCurrencies.contains(input.currency());
  }
}
