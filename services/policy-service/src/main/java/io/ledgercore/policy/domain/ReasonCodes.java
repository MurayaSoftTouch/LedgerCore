package io.ledgercore.policy.domain;

import java.util.regex.Pattern;

/** Reason codes: the contract's pattern, plus the codes the engine itself emits. */
public final class ReasonCodes {

  /** No policy is scoped to the organization and no default policy exists. */
  public static final String NO_APPLICABLE_POLICY = "NO_APPLICABLE_POLICY";

  /** The applicable policy exists but has no ACTIVE version. */
  public static final String NO_ACTIVE_POLICY_VERSION = "NO_ACTIVE_POLICY_VERSION";

  /** The request carries a transaction type this service does not know (a later 1.x value). */
  public static final String TRANSACTION_TYPE_UNSUPPORTED = "TRANSACTION_TYPE_UNSUPPORTED";

  private static final Pattern PATTERN = Pattern.compile("^[A-Z][A-Z0-9_]{1,63}$");

  private ReasonCodes() {}

  public static String require(String code) {
    if (code == null || !PATTERN.matcher(code).matches()) {
      throw PolicyDomainException.invalid(
          "REASON_CODE_INVALID",
          "reasonCode must match ^[A-Z][A-Z0-9_]{1,63}$ (got '" + code + "').");
    }
    return code;
  }
}
