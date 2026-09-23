package io.ledgercore.policy.domain;

/** How a {@link PolicyDomainException} maps onto a caller-visible outcome. */
public enum ErrorKind {
  /** The input violates a rule regardless of state (HTTP 400). */
  INVALID,
  /** The target does not exist (HTTP 404). */
  NOT_FOUND,
  /** The operation is not allowed in the current state, or collides with existing data (409). */
  CONFLICT
}
