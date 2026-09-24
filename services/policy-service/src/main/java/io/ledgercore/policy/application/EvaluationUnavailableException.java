package io.ledgercore.policy.application;

/**
 * The service cannot produce a trustworthy decision (store unavailable, invalid stored
 * configuration, unexpected failure). Mapped to HTTP 503 so the ledger fails closed (ADR-005); it
 * is never turned into a decision.
 */
public final class EvaluationUnavailableException extends RuntimeException {

  private static final long serialVersionUID = 1L;

  public EvaluationUnavailableException(String message, Throwable cause) {
    super(message, cause);
  }
}
