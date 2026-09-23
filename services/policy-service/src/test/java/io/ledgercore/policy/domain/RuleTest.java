package io.ledgercore.policy.domain;

import static io.ledgercore.policy.domain.RuleOutcome.REJECTED;
import static io.ledgercore.policy.domain.RuleOutcome.REVIEW_REQUIRED;
import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import io.ledgercore.policy.domain.EvaluationInput.AccountRef;
import io.ledgercore.policy.domain.rules.AccountContextRule;
import io.ledgercore.policy.domain.rules.AmountAboveRule;
import io.ledgercore.policy.domain.rules.CurrencyNotAllowedRule;
import io.ledgercore.policy.domain.rules.TransactionTypeRule;
import java.math.BigDecimal;
import java.util.EnumSet;
import java.util.List;
import java.util.Set;
import java.util.UUID;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.junit.jupiter.params.provider.ValueSource;

class RuleTest {

  private static AmountAboveRule above(String threshold) {
    return new AmountAboveRule(1, REVIEW_REQUIRED, "AMOUNT_OVER", "KES", new BigDecimal(threshold));
  }

  @ParameterizedTest
  @CsvSource({
    "10000.00, 10000.01, true",
    "10000.00, 10000.00, false",
    "10000.00, 10000, false",
    "10000.00, 9999.9999, false",
    "0, 0.0001, true",
    "999999999999999999.9998, 999999999999999999.9999, true"
  })
  void amountThresholdIsStrictlyGreaterAndExact(String threshold, String amount, boolean matches) {
    assertThat(above(threshold).matches(Inputs.payment(amount))).isEqualTo(matches);
  }

  @Test
  void amountThresholdIgnoresOtherCurrencies() {
    assertThat(above("10").matches(Inputs.input("PAYMENT", "USD", "1000000"))).isFalse();
  }

  @ParameterizedTest
  @ValueSource(strings = {"-1", "1.00001", "1000000000000000000"})
  void amountThresholdRejectsInvalidThreshold(String threshold) {
    assertThatThrownBy(() -> above(threshold))
        .isInstanceOfSatisfying(
            PolicyDomainException.class,
            e -> assertThat(e.code()).isEqualTo("RULE_THRESHOLD_INVALID"));
  }

  @Test
  void transactionTypeRuleMatchesConfiguredTypesOnly() {
    var rule =
        new TransactionTypeRule(
            1, REVIEW_REQUIRED, "ADJUSTMENT_NEEDS_REVIEW", EnumSet.of(TransactionType.ADJUSTMENT));

    assertThat(rule.matches(Inputs.input("ADJUSTMENT", "KES", "1"))).isTrue();
    assertThat(rule.matches(Inputs.input("PAYMENT", "KES", "1"))).isFalse();
    assertThat(rule.matches(Inputs.input("WIRE", "KES", "1"))).isFalse();
  }

  @Test
  void accountContextMatchesWhenAnyAccountMeetsAllCriteria() {
    var equityDebit =
        new AccountContextRule(
            1, REJECTED, "EQUITY_DEBIT_BLOCKED", Set.of(AccountType.EQUITY), EntrySide.DEBIT, null);

    assertThat(
            equityDebit.matches(
                Inputs.withAccounts(
                    List.of(
                        new AccountRef(UUID.randomUUID(), AccountType.EQUITY, EntrySide.DEBIT),
                        new AccountRef(UUID.randomUUID(), AccountType.ASSET, EntrySide.CREDIT)))))
        .isTrue();
    assertThat(
            equityDebit.matches(
                Inputs.withAccounts(
                    List.of(
                        new AccountRef(UUID.randomUUID(), AccountType.EQUITY, EntrySide.CREDIT),
                        new AccountRef(UUID.randomUUID(), AccountType.ASSET, EntrySide.DEBIT)))))
        .isFalse();
  }

  @Test
  void accountContextCanTargetSpecificAccounts() {
    var blocked =
        new AccountContextRule(1, REJECTED, "ACCOUNT_FROZEN", null, null, Set.of(Inputs.CASH));

    assertThat(blocked.matches(Inputs.payment("1"))).isTrue();
    assertThat(
            blocked.matches(
                Inputs.withAccounts(
                    List.of(
                        new AccountRef(UUID.randomUUID(), AccountType.ASSET, EntrySide.DEBIT),
                        new AccountRef(UUID.randomUUID(), AccountType.ASSET, EntrySide.CREDIT)))))
        .isFalse();
  }

  @Test
  void accountContextRequiresCriteria() {
    assertThatThrownBy(() -> new AccountContextRule(1, REJECTED, "X_Y", Set.of(), null, Set.of()))
        .isInstanceOfSatisfying(
            PolicyDomainException.class,
            e -> assertThat(e.code()).isEqualTo("RULE_ACCOUNT_CRITERIA_REQUIRED"));
  }

  @Test
  void currencyRuleMatchesCurrenciesOutsideTheAllowList() {
    var rule =
        new CurrencyNotAllowedRule(1, REJECTED, "CURRENCY_NOT_ALLOWED", Set.of("KES", "USD"));

    assertThat(rule.matches(Inputs.input("PAYMENT", "EUR", "1"))).isTrue();
    assertThat(rule.matches(Inputs.input("PAYMENT", "KES", "1"))).isFalse();
  }

  @ParameterizedTest
  @ValueSource(strings = {"", "lowercase", "1STARTS_WITH_DIGIT", "A", "HAS-DASH"})
  void reasonCodesMustMatchContractPattern(String code) {
    assertThatThrownBy(() -> new AmountAboveRule(1, REJECTED, code, "KES", BigDecimal.TEN))
        .isInstanceOfSatisfying(
            PolicyDomainException.class,
            e -> assertThat(e.code()).isEqualTo("REASON_CODE_INVALID"));
  }

  @ParameterizedTest
  @ValueSource(ints = {0, -1, 101})
  void positionsAreBounded(int position) {
    assertThatThrownBy(() -> new AmountAboveRule(position, REJECTED, "X_Y", "KES", BigDecimal.TEN))
        .isInstanceOf(PolicyDomainException.class);
  }
}
