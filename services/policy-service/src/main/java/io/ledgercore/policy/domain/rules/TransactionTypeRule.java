package io.ledgercore.policy.domain.rules;

import io.ledgercore.policy.domain.EvaluationInput;
import io.ledgercore.policy.domain.PolicyDomainException;
import io.ledgercore.policy.domain.RuleOutcome;
import io.ledgercore.policy.domain.TransactionType;
import java.util.Collections;
import java.util.EnumSet;
import java.util.Set;

/** Matches when the request's transaction type is one of {@code transactionTypes}. */
public record TransactionTypeRule(
    int position, RuleOutcome outcome, String reasonCode, Set<TransactionType> transactionTypes)
    implements Rule {

  public TransactionTypeRule {
    Rule.validateCommon(position, outcome, reasonCode);
    if (transactionTypes == null || transactionTypes.isEmpty()) {
      throw PolicyDomainException.invalid(
          "RULE_TRANSACTION_TYPES_REQUIRED", "TRANSACTION_TYPE requires at least one type.");
    }
    transactionTypes = Collections.unmodifiableSet(EnumSet.copyOf(transactionTypes));
  }

  @Override
  public RuleType type() {
    return RuleType.TRANSACTION_TYPE;
  }

  @Override
  public boolean matches(EvaluationInput input) {
    return TransactionType.fromContract(input.transactionType())
        .map(transactionTypes::contains)
        .orElse(false);
  }
}
