package io.ledgercore.policy.domain;

import io.ledgercore.policy.domain.rules.Rule;
import java.util.List;

/**
 * The result of evaluating one input.
 *
 * @param matchedRules rules that matched, in position order
 * @param reasonCodes distinct codes, most severe first, then by rule position; empty only when
 *     {@code decision} is APPROVED (contract v1)
 */
public record Evaluation(Decision decision, List<Rule> matchedRules, List<String> reasonCodes) {

  public Evaluation {
    matchedRules = List.copyOf(matchedRules);
    reasonCodes = List.copyOf(reasonCodes);
    if ((decision == Decision.APPROVED) != reasonCodes.isEmpty()) {
      throw new IllegalStateException("APPROVED has no reason codes; other decisions need one.");
    }
  }

  /** Fail-safe outcome when no rules can be applied (no policy, no active version). */
  public static Evaluation reviewRequired(String reasonCode) {
    return new Evaluation(Decision.REVIEW_REQUIRED, List.of(), List.of(reasonCode));
  }
}
