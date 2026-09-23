package io.ledgercore.policy.domain;

import io.ledgercore.policy.domain.EvaluationInput.AccountRef;
import java.math.BigDecimal;
import java.util.List;
import java.util.UUID;

final class Inputs {

  static final UUID ORG = UUID.fromString("0c9e4f21-5a7b-4d3e-8f1a-2b6c9d0e4a5b");
  static final UUID EXPENSE = UUID.fromString("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");
  static final UUID CASH = UUID.fromString("b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e");

  private Inputs() {}

  static EvaluationInput payment(String amount) {
    return input("PAYMENT", "KES", amount);
  }

  static EvaluationInput input(String type, String currency, String amount) {
    return new EvaluationInput(
        ORG,
        type,
        currency,
        new BigDecimal(amount),
        List.of(
            new AccountRef(EXPENSE, AccountType.EXPENSE, EntrySide.DEBIT),
            new AccountRef(CASH, AccountType.ASSET, EntrySide.CREDIT)));
  }

  static EvaluationInput withAccounts(List<AccountRef> accounts) {
    return new EvaluationInput(ORG, "PAYMENT", "KES", new BigDecimal("10"), accounts);
  }
}
