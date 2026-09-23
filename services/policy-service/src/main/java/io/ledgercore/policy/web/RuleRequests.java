package io.ledgercore.policy.web;

import io.ledgercore.policy.domain.PolicyDomainException;
import io.ledgercore.policy.domain.rules.AccountContextRule;
import io.ledgercore.policy.domain.rules.AmountAboveRule;
import io.ledgercore.policy.domain.rules.CurrencyNotAllowedRule;
import io.ledgercore.policy.domain.rules.Rule;
import io.ledgercore.policy.domain.rules.TransactionTypeRule;
import io.ledgercore.policy.web.ManagementContract.RuleRequest;
import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.EnumSet;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.regex.Pattern;

/** Maps management requests to domain rules. Position is the rule's 1-based index in the list. */
final class RuleRequests {

  private static final Pattern DECIMAL = Pattern.compile("^(0|[1-9]\\d{0,17})(\\.\\d{1,4})?$");

  private RuleRequests() {}

  static List<Rule> toRules(List<RuleRequest> requests) {
    var rules = new ArrayList<Rule>(requests.size());
    for (int i = 0; i < requests.size(); i++) {
      rules.add(toRule(i + 1, requests.get(i)));
    }
    return rules;
  }

  private static Rule toRule(int position, RuleRequest r) {
    return switch (r.type()) {
      case AMOUNT_ABOVE -> {
        onlyAllowed(position, r, "currency", "threshold");
        yield new AmountAboveRule(
            position, r.outcome(), r.reasonCode(), r.currency(), decimal(position, r.threshold()));
      }
      case TRANSACTION_TYPE -> {
        onlyAllowed(position, r, "transactionTypes");
        yield new TransactionTypeRule(
            position,
            r.outcome(),
            r.reasonCode(),
            r.transactionTypes() == null || r.transactionTypes().isEmpty()
                ? null
                : EnumSet.copyOf(r.transactionTypes()));
      }
      case ACCOUNT_CONTEXT -> {
        onlyAllowed(position, r, "accountTypes", "side", "accountIds");
        yield new AccountContextRule(
            position,
            r.outcome(),
            r.reasonCode(),
            r.accountTypes() == null || r.accountTypes().isEmpty()
                ? null
                : EnumSet.copyOf(r.accountTypes()),
            r.side(),
            r.accountIds() == null ? null : new HashSet<>(r.accountIds()));
      }
      case CURRENCY_NOT_ALLOWED -> {
        onlyAllowed(position, r, "allowedCurrencies");
        yield new CurrencyNotAllowedRule(
            position,
            r.outcome(),
            r.reasonCode(),
            r.allowedCurrencies() == null ? null : new HashSet<>(r.allowedCurrencies()));
      }
    };
  }

  private static BigDecimal decimal(int position, String value) {
    if (value == null || !DECIMAL.matcher(value).matches()) {
      throw PolicyDomainException.invalid(
          "RULE_THRESHOLD_INVALID",
          "Rule " + position + ": threshold must be a decimal string, e.g. \"10000.00\".");
    }
    return new BigDecimal(value);
  }

  /** Rejects any field set on the request that the rule type does not use. */
  private static void onlyAllowed(int position, RuleRequest r, String... allowed) {
    Map<String, Object> fields = new java.util.LinkedHashMap<>();
    fields.put("currency", r.currency());
    fields.put("threshold", r.threshold());
    fields.put("transactionTypes", r.transactionTypes());
    fields.put("accountTypes", r.accountTypes());
    fields.put("side", r.side());
    fields.put("accountIds", r.accountIds());
    fields.put("allowedCurrencies", r.allowedCurrencies());
    var permitted = List.of(allowed);
    for (var field : fields.entrySet()) {
      if (field.getValue() != null && !permitted.contains(field.getKey())) {
        throw PolicyDomainException.invalid(
            "RULE_FIELD_NOT_ALLOWED",
            "Rule " + position + ": " + field.getKey() + " is not used by " + r.type() + ".");
      }
    }
  }
}
