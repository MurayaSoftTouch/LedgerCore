package io.ledgercore.policy.domain;

import io.ledgercore.policy.domain.rules.Rule;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashSet;
import java.util.List;

/**
 * Deterministic evaluation (ADR-010). A pure function of (version rules, input): no clock, no I/O,
 * no dependence on storage order.
 *
 * <ol>
 *   <li>Evaluate every rule, in ascending position.
 *   <li>The decision is the most restrictive outcome among matches; no match means APPROVED.
 *   <li>A transaction type unknown to this service adds REVIEW_REQUIRED
 *       (TRANSACTION_TYPE_UNSUPPORTED), per ADR-005.
 *   <li>Reason codes: most severe first, then by position; duplicates removed.
 * </ol>
 */
public final class PolicyEvaluator {

  private PolicyEvaluator() {}

  public static Evaluation evaluate(List<Rule> rules, EvaluationInput input) {
    var ordered = rules.stream().sorted(Comparator.comparingInt(Rule::position)).toList();
    var matched = new ArrayList<Rule>();
    for (var rule : ordered) {
      if (rule.matches(input)) {
        matched.add(rule);
      }
    }

    var decision = Decision.APPROVED;
    var systemCodes = new ArrayList<String>();
    if (TransactionType.fromContract(input.transactionType()).isEmpty()) {
      decision = Decision.REVIEW_REQUIRED;
      systemCodes.add(ReasonCodes.TRANSACTION_TYPE_UNSUPPORTED);
    }
    for (var rule : matched) {
      decision = decision.mostRestrictive(rule.outcome().decision());
    }

    var reasons = new LinkedHashSet<String>();
    matched.stream()
        .filter(r -> r.outcome() == RuleOutcome.REJECTED)
        .forEach(r -> reasons.add(r.reasonCode()));
    reasons.addAll(systemCodes);
    matched.stream()
        .filter(r -> r.outcome() == RuleOutcome.REVIEW_REQUIRED)
        .forEach(r -> reasons.add(r.reasonCode()));

    return new Evaluation(decision, matched, List.copyOf(reasons));
  }
}
