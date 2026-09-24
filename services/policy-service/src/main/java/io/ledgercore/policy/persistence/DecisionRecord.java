package io.ledgercore.policy.persistence;

import io.ledgercore.policy.domain.Decision;
import io.ledgercore.policy.domain.RuleOutcome;
import java.math.BigDecimal;
import java.time.Instant;
import java.util.List;
import java.util.UUID;

/**
 * An immutable, persisted decision (ADR-011).
 *
 * @param policyId the policy that governed the request, or {@code null} if none applied
 * @param policyVersionId the version that produced the decision, or {@code null} if no version
 *     could (then the decision is REVIEW_REQUIRED and {@code policyVersionLabel} is {@code none})
 */
public record DecisionRecord(
    UUID id,
    UUID transactionId,
    UUID organizationId,
    String requestFingerprint,
    UUID policyId,
    UUID policyVersionId,
    String policyVersionLabel,
    Decision decision,
    List<String> reasonCodes,
    String transactionType,
    String currency,
    BigDecimal totalAmount,
    String contractVersion,
    String correlationId,
    Instant evaluatedAt,
    List<Match> matches) {

  public DecisionRecord {
    reasonCodes = List.copyOf(reasonCodes);
    matches = List.copyOf(matches);
  }

  /** A rule that matched, identified by its position within the decision's version. */
  public record Match(int position, RuleOutcome outcome, String reasonCode) {}
}
