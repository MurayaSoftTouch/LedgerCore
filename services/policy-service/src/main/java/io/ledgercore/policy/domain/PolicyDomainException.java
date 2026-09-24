package io.ledgercore.policy.domain;

/** A violated policy-domain rule. {@link #code()} is stable and machine-readable. */
public final class PolicyDomainException extends RuntimeException {

  private static final long serialVersionUID = 1L;

  private final ErrorKind kind;
  private final String code;

  public PolicyDomainException(ErrorKind kind, String code, String message) {
    super(message);
    this.kind = kind;
    this.code = code;
  }

  public static PolicyDomainException invalid(String code, String message) {
    return new PolicyDomainException(ErrorKind.INVALID, code, message);
  }

  public static PolicyDomainException notFound(String code, String message) {
    return new PolicyDomainException(ErrorKind.NOT_FOUND, code, message);
  }

  public static PolicyDomainException conflict(String code, String message) {
    return new PolicyDomainException(ErrorKind.CONFLICT, code, message);
  }

  public ErrorKind kind() {
    return kind;
  }

  public String code() {
    return code;
  }
}
