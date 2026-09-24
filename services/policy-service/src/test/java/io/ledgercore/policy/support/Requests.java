package io.ledgercore.policy.support;

import java.util.UUID;

/** Contract-v1 request bodies. */
public final class Requests {

  public static final UUID EXPENSE_ACCOUNT =
      UUID.fromString("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");
  public static final UUID CASH_ACCOUNT = UUID.fromString("b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e");

  private Requests() {}

  public static String decision(
      UUID transactionId, UUID organizationId, String type, String amount) {
    return decision(transactionId, organizationId, type, "KES", amount, "EXPENSE", "ASSET");
  }

  public static String decision(
      UUID transactionId,
      UUID organizationId,
      String type,
      String currency,
      String amount,
      String debitAccountType,
      String creditAccountType) {
    return """
        {
          "contractVersion": "1.0.0",
          "transactionId": "%s",
          "organizationId": "%s",
          "transactionType": "%s",
          "currency": "%s",
          "totalAmount": "%s",
          "accountContext": [
            {"accountId": "%s", "accountType": "%s", "side": "DEBIT"},
            {"accountId": "%s", "accountType": "%s", "side": "CREDIT"}
          ],
          "requestedBy": {"principalId": "user-4821", "principalType": "USER"},
          "requestedAt": "2026-09-23T09:15:00Z"
        }
        """
        .formatted(
            transactionId,
            organizationId,
            type,
            currency,
            amount,
            EXPENSE_ACCOUNT,
            debitAccountType,
            CASH_ACCOUNT,
            creditAccountType);
  }

  /**
   * Review above 10k KES, reject above 50k KES, review adjustments, KES/USD only, no equity debits.
   */
  public static final String STANDARD_RULES =
      """
      [
        {"type": "AMOUNT_ABOVE", "currency": "KES", "threshold": "10000.00",
         "outcome": "REVIEW_REQUIRED", "reasonCode": "AMOUNT_EXCEEDS_REVIEW_THRESHOLD"},
        {"type": "AMOUNT_ABOVE", "currency": "KES", "threshold": "50000.00",
         "outcome": "REJECTED", "reasonCode": "AMOUNT_EXCEEDS_HARD_LIMIT"},
        {"type": "TRANSACTION_TYPE", "transactionTypes": ["ADJUSTMENT"],
         "outcome": "REVIEW_REQUIRED", "reasonCode": "TRANSACTION_TYPE_REQUIRES_REVIEW"},
        {"type": "CURRENCY_NOT_ALLOWED", "allowedCurrencies": ["KES", "USD"],
         "outcome": "REJECTED", "reasonCode": "CURRENCY_NOT_ALLOWED"},
        {"type": "ACCOUNT_CONTEXT", "accountTypes": ["EQUITY"], "side": "DEBIT",
         "outcome": "REJECTED", "reasonCode": "ACCOUNT_CONTEXT_RESTRICTED"}
      ]
      """;
}
