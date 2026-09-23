package io.ledgercore.policy.domain;

import static io.ledgercore.policy.domain.RuleOutcome.REJECTED;
import static io.ledgercore.policy.domain.RuleOutcome.REVIEW_REQUIRED;
import static org.assertj.core.api.Assertions.assertThat;

import io.ledgercore.policy.domain.rules.AccountContextRule;
import io.ledgercore.policy.domain.rules.AmountAboveRule;
import io.ledgercore.policy.domain.rules.CurrencyNotAllowedRule;
import io.ledgercore.policy.domain.rules.Rule;
import io.ledgercore.policy.domain.rules.TransactionTypeRule;
import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.Collections;
import java.util.EnumSet;
import java.util.List;
import java.util.Random;
import java.util.Set;
import org.junit.jupiter.api.RepeatedTest;
import org.junit.jupiter.api.Test;

class PolicyEvaluatorTest {

  /** A realistic policy: review above 10k, reject above 50k, review adjustments, KES/USD only. */
  private static final List<Rule> RULES =
      List.of(
          new AmountAboveRule(
              1,
              REVIEW_REQUIRED,
              "AMOUNT_EXCEEDS_REVIEW_THRESHOLD",
              "KES",
              new BigDecimal("10000")),
          new AmountAboveRule(
              2, REJECTED, "AMOUNT_EXCEEDS_HARD_LIMIT", "KES", new BigDecimal("50000")),
          new TransactionTypeRule(
              3,
              REVIEW_REQUIRED,
              "TRANSACTION_TYPE_REQUIRES_REVIEW",
              EnumSet.of(TransactionType.ADJUSTMENT)),
          new CurrencyNotAllowedRule(4, REJECTED, "CURRENCY_NOT_ALLOWED", Set.of("KES", "USD")),
          new AccountContextRule(
              5,
              REJECTED,
              "ACCOUNT_CONTEXT_RESTRICTED",
              Set.of(AccountType.EQUITY),
              EntrySide.DEBIT,
              null));

  @Test
  void noMatchingRuleApproves() {
    var result = PolicyEvaluator.evaluate(RULES, Inputs.payment("9999.99"));

    assertThat(result.decision()).isEqualTo(Decision.APPROVED);
    assertThat(result.reasonCodes()).isEmpty();
    assertThat(result.matchedRules()).isEmpty();
  }

  @Test
  void singleReviewRuleRequiresReview() {
    var result = PolicyEvaluator.evaluate(RULES, Inputs.payment("10000.01"));

    assertThat(result.decision()).isEqualTo(Decision.REVIEW_REQUIRED);
    assertThat(result.reasonCodes()).containsExactly("AMOUNT_EXCEEDS_REVIEW_THRESHOLD");
  }

  @Test
  void mostRestrictiveOutcomeWinsAndAllMatchesAreReported() {
    var result = PolicyEvaluator.evaluate(RULES, Inputs.input("ADJUSTMENT", "KES", "60000"));

    assertThat(result.decision()).isEqualTo(Decision.REJECTED);
    assertThat(result.matchedRules()).extracting(Rule::position).containsExactly(1, 2, 3);
    assertThat(result.reasonCodes())
        .containsExactly(
            "AMOUNT_EXCEEDS_HARD_LIMIT",
            "AMOUNT_EXCEEDS_REVIEW_THRESHOLD",
            "TRANSACTION_TYPE_REQUIRES_REVIEW");
  }

  @Test
  void disallowedCurrencyIsRejected() {
    var result = PolicyEvaluator.evaluate(RULES, Inputs.input("PAYMENT", "EUR", "5"));

    assertThat(result.decision()).isEqualTo(Decision.REJECTED);
    assertThat(result.reasonCodes()).containsExactly("CURRENCY_NOT_ALLOWED");
  }

  @Test
  void unknownTransactionTypeRequiresReviewEvenWithNoRules() {
    var result = PolicyEvaluator.evaluate(List.of(), Inputs.input("WIRE", "KES", "1"));

    assertThat(result.decision()).isEqualTo(Decision.REVIEW_REQUIRED);
    assertThat(result.reasonCodes()).containsExactly(ReasonCodes.TRANSACTION_TYPE_UNSUPPORTED);
  }

  @Test
  void unknownTransactionTypeDoesNotMaskARejection() {
    var result = PolicyEvaluator.evaluate(RULES, Inputs.input("WIRE", "KES", "60000"));

    assertThat(result.decision()).isEqualTo(Decision.REJECTED);
    assertThat(result.reasonCodes())
        .containsExactly(
            "AMOUNT_EXCEEDS_HARD_LIMIT",
            ReasonCodes.TRANSACTION_TYPE_UNSUPPORTED,
            "AMOUNT_EXCEEDS_REVIEW_THRESHOLD");
  }

  @Test
  void sharedReasonCodesAreReportedOnce() {
    var rules =
        List.<Rule>of(
            new AmountAboveRule(1, REVIEW_REQUIRED, "LARGE", "KES", new BigDecimal("1")),
            new AmountAboveRule(2, REVIEW_REQUIRED, "LARGE", "KES", new BigDecimal("2")));

    assertThat(PolicyEvaluator.evaluate(rules, Inputs.payment("3")).reasonCodes())
        .containsExactly("LARGE");
  }

  @RepeatedTest(20)
  void resultDoesNotDependOnRuleStorageOrder() {
    var shuffled = new ArrayList<>(RULES);
    Collections.shuffle(shuffled, new Random());
    var input = Inputs.input("ADJUSTMENT", "KES", "60000");

    assertThat(PolicyEvaluator.evaluate(shuffled, input))
        .isEqualTo(PolicyEvaluator.evaluate(RULES, input));
  }
}
