package io.ledgercore.policy.application;

import java.util.UUID;

/**
 * A decision already exists for this transaction but the new request differs in a decision-relevant
 * field. The stored decision is left untouched (ADR-011).
 */
public final class IdempotencyConflictException extends RuntimeException {

  private static final long serialVersionUID = 1L;

  private final UUID transactionId;
  private final UUID existingDecisionId;

  public IdempotencyConflictException(UUID transactionId, UUID existingDecisionId) {
    super(
        "Transaction "
            + transactionId
            + " was already evaluated with different decision inputs (decision "
            + existingDecisionId
            + ").");
    this.transactionId = transactionId;
    this.existingDecisionId = existingDecisionId;
  }

  public UUID transactionId() {
    return transactionId;
  }

  public UUID existingDecisionId() {
    return existingDecisionId;
  }
}
