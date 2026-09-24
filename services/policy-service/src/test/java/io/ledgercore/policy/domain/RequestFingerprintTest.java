package io.ledgercore.policy.domain;

import static org.assertj.core.api.Assertions.assertThat;

import io.ledgercore.policy.domain.EvaluationInput.AccountRef;
import java.math.BigDecimal;
import java.util.List;
import org.junit.jupiter.api.Test;

class RequestFingerprintTest {

  @Test
  void numericallyEqualAmountsAndReorderedAccountsAreIdentical() {
    var a = Inputs.payment("100.50");
    var reordered =
        new EvaluationInput(
            a.organizationId(),
            "PAYMENT",
            "KES",
            new BigDecimal("100.5000"),
            List.of(a.accountContext().get(1), a.accountContext().get(0)));

    assertThat(RequestFingerprint.of(reordered)).isEqualTo(RequestFingerprint.of(a));
  }

  @Test
  void anyDecisionRelevantChangeAltersTheFingerprint() {
    var base = RequestFingerprint.of(Inputs.payment("100.50"));

    assertThat(RequestFingerprint.of(Inputs.payment("100.51"))).isNotEqualTo(base);
    assertThat(RequestFingerprint.of(Inputs.input("FEE", "KES", "100.50"))).isNotEqualTo(base);
    assertThat(RequestFingerprint.of(Inputs.input("PAYMENT", "USD", "100.50"))).isNotEqualTo(base);
    assertThat(
            RequestFingerprint.of(
                Inputs.withAccounts(
                    List.of(
                        new AccountRef(Inputs.EXPENSE, AccountType.EXPENSE, EntrySide.CREDIT),
                        new AccountRef(Inputs.CASH, AccountType.ASSET, EntrySide.DEBIT)))))
        .isNotEqualTo(
            RequestFingerprint.of(Inputs.withAccounts(Inputs.payment("10").accountContext())));
  }

  @Test
  void fingerprintIsHexSha256() {
    assertThat(RequestFingerprint.of(Inputs.payment("1"))).matches("^[0-9a-f]{64}$");
  }
}
