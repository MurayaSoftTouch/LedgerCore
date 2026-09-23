package io.ledgercore.policy.domain;

import java.math.BigDecimal;
import java.util.List;
import java.util.Objects;
import java.util.UUID;

/**
 * The contract-v1 facts a policy may look at. Nothing else from the request (principal, timestamps)
 * influences a decision.
 *
 * @param transactionType the raw contract value; may be a type this service does not know yet
 */
public record EvaluationInput(
    UUID organizationId,
    String transactionType,
    String currency,
    BigDecimal totalAmount,
    List<AccountRef> accountContext) {

  public EvaluationInput {
    Objects.requireNonNull(organizationId, "organizationId");
    Objects.requireNonNull(transactionType, "transactionType");
    Objects.requireNonNull(currency, "currency");
    Objects.requireNonNull(totalAmount, "totalAmount");
    accountContext = List.copyOf(accountContext);
  }

  /** One account touched by the journal. */
  public record AccountRef(UUID accountId, AccountType accountType, EntrySide side) {
    public AccountRef {
      Objects.requireNonNull(accountId, "accountId");
      Objects.requireNonNull(accountType, "accountType");
      Objects.requireNonNull(side, "side");
    }
  }
}
