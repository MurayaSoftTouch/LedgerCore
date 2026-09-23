package io.ledgercore.policy.domain.rules;

/** The closed set of rule types. Adding one is a code change, never configuration. */
public enum RuleType {
  AMOUNT_ABOVE,
  TRANSACTION_TYPE,
  ACCOUNT_CONTEXT,
  CURRENCY_NOT_ALLOWED
}
