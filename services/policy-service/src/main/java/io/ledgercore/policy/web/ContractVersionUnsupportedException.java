package io.ledgercore.policy.web;

/** The request's {@code contractVersion} is not a 1.x version (contract v1: HTTP 409). */
public final class ContractVersionUnsupportedException extends RuntimeException {

  private static final long serialVersionUID = 1L;

  public ContractVersionUnsupportedException(String version) {
    super("contractVersion '" + version + "' is not supported; this service implements 1.x.");
  }
}
