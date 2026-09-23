package io.ledgercore.policy.support;

/** Throwaway credentials for tests only. Real values come from the environment (ADR-013). */
public final class TestTokens {

  public static final String DECISION = "test-decision-token-0000000000000000000000";
  public static final String ADMIN = "test-admin-token-11111111111111111111111111";
  public static final String DECISION_BEARER = "Bearer " + DECISION;
  public static final String ADMIN_BEARER = "Bearer " + ADMIN;

  private TestTokens() {}
}
