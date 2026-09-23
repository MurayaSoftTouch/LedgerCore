package io.ledgercore.policy.domain;

import java.util.Arrays;
import java.util.Optional;

/**
 * Transaction types defined by contract v1. Requests may carry a value added in a later 1.x minor
 * version; such values are tolerated and evaluated as {@code REVIEW_REQUIRED} (ADR-005).
 */
public enum TransactionType {
  PAYMENT,
  TRANSFER,
  ADJUSTMENT,
  REVERSAL,
  FEE;

  public static Optional<TransactionType> fromContract(String value) {
    return Arrays.stream(values()).filter(t -> t.name().equals(value)).findFirst();
  }
}
