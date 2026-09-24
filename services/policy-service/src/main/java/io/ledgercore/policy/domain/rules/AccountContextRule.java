package io.ledgercore.policy.domain.rules;

import io.ledgercore.policy.domain.AccountType;
import io.ledgercore.policy.domain.EntrySide;
import io.ledgercore.policy.domain.EvaluationInput;
import io.ledgercore.policy.domain.EvaluationInput.AccountRef;
import io.ledgercore.policy.domain.PolicyDomainException;
import io.ledgercore.policy.domain.RuleOutcome;
import java.util.Collections;
import java.util.EnumSet;
import java.util.Set;
import java.util.TreeSet;
import java.util.UUID;

/**
 * Matches when <em>any</em> account in the request's accountContext satisfies every configured
 * criterion: its type is in {@code accountTypes} (if given), its side equals {@code side} (if
 * given), and its id is in {@code accountIds} (if given). Uses only contract-v1 fields: account id,
 * account type and side.
 */
public record AccountContextRule(
    int position,
    RuleOutcome outcome,
    String reasonCode,
    Set<AccountType> accountTypes,
    EntrySide side,
    Set<UUID> accountIds)
    implements Rule {

  public AccountContextRule {
    Rule.validateCommon(position, outcome, reasonCode);
    accountTypes =
        accountTypes == null || accountTypes.isEmpty()
            ? Set.of()
            : Collections.unmodifiableSet(EnumSet.copyOf(accountTypes));
    accountIds =
        accountIds == null ? Set.of() : Collections.unmodifiableSet(new TreeSet<>(accountIds));
    if (accountTypes.isEmpty() && accountIds.isEmpty()) {
      throw PolicyDomainException.invalid(
          "RULE_ACCOUNT_CRITERIA_REQUIRED",
          "ACCOUNT_CONTEXT requires accountTypes and/or accountIds.");
    }
  }

  @Override
  public RuleType type() {
    return RuleType.ACCOUNT_CONTEXT;
  }

  @Override
  public boolean matches(EvaluationInput input) {
    return input.accountContext().stream().anyMatch(this::matchesAccount);
  }

  private boolean matchesAccount(AccountRef account) {
    return (accountTypes.isEmpty() || accountTypes.contains(account.accountType()))
        && (side == null || side == account.side())
        && (accountIds.isEmpty() || accountIds.contains(account.accountId()));
  }
}
