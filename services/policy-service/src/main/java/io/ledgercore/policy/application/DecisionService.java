package io.ledgercore.policy.application;

import io.ledgercore.policy.domain.Evaluation;
import io.ledgercore.policy.domain.EvaluationInput;
import io.ledgercore.policy.domain.Policy;
import io.ledgercore.policy.domain.PolicyEvaluator;
import io.ledgercore.policy.domain.PolicyVersion;
import io.ledgercore.policy.domain.ReasonCodes;
import io.ledgercore.policy.domain.RequestFingerprint;
import io.ledgercore.policy.persistence.DecisionRecord;
import io.ledgercore.policy.persistence.DecisionRecord.Match;
import io.ledgercore.policy.persistence.DecisionRepository;
import io.ledgercore.policy.persistence.PolicyRepository;
import io.ledgercore.policy.persistence.PolicyVersionRepository;
import java.time.Clock;
import java.time.temporal.ChronoUnit;
import java.util.Optional;
import java.util.UUID;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

/**
 * Evaluates and records decisions (ADR-010, ADR-011).
 *
 * <p>Selection: the organization's own policy if it has one, otherwise the default policy. An
 * organization policy without an ACTIVE version does <em>not</em> fall back to the default: it
 * yields REVIEW_REQUIRED (NO_ACTIVE_POLICY_VERSION), because a missing configuration must never
 * become a more permissive one.
 *
 * <p>Binding: the version is the one ACTIVE at first evaluation (share-locked until commit). The
 * request's {@code requestedAt} is not used to choose a version. A repeated identical request
 * returns the stored decision, so a transaction's decision never changes as policies evolve.
 */
@Service
public class DecisionService {

  /** Contract {@code policyVersion} value when no policy version could decide. */
  public static final String NO_VERSION_LABEL = "none";

  private static final Logger log = LoggerFactory.getLogger(DecisionService.class);

  private final PolicyRepository policies;
  private final PolicyVersionRepository versions;
  private final DecisionRepository decisions;
  private final Clock clock;

  public DecisionService(
      PolicyRepository policies,
      PolicyVersionRepository versions,
      DecisionRepository decisions,
      Clock clock) {
    this.policies = policies;
    this.versions = versions;
    this.decisions = decisions;
    this.clock = clock;
  }

  /**
   * @throws IdempotencyConflictException the transaction was decided with different inputs
   * @throws EvaluationUnavailableException anything else went wrong; never a guessed decision
   */
  @Transactional
  public DecisionOutcome evaluate(
      UUID transactionId, EvaluationInput input, String contractVersion, String correlationId) {
    var fingerprint = RequestFingerprint.of(input);
    try {
      var existing = decisions.findByTransactionId(transactionId);
      if (existing.isPresent()) {
        return replay(existing.get(), fingerprint);
      }

      var candidate = decide(transactionId, input, fingerprint, contractVersion, correlationId);
      if (decisions.insertIfAbsent(candidate)) {
        logDecision(candidate, false);
        return new DecisionOutcome(candidate, false);
      }
      // A concurrent request for the same transaction committed first; its decision stands.
      var winner =
          decisions
              .findByTransactionId(transactionId)
              .orElseThrow(() -> new IllegalStateException("conflicting insert vanished"));
      return replay(winner, fingerprint);
    } catch (IdempotencyConflictException e) {
      throw e;
    } catch (RuntimeException e) {
      log.atError()
          .addKeyValue("transactionId", transactionId)
          .addKeyValue("errorType", e.getClass().getName())
          .setCause(e)
          .log("Policy evaluation failed; no decision recorded");
      throw new EvaluationUnavailableException("Policy evaluation is unavailable.", e);
    }
  }

  @Transactional(readOnly = true)
  public Optional<DecisionRecord> find(UUID decisionId) {
    return decisions.findById(decisionId);
  }

  private DecisionRecord decide(
      UUID transactionId,
      EvaluationInput input,
      String fingerprint,
      String contractVersion,
      String correlationId) {
    Optional<Policy> policy = policies.findApplicable(input.organizationId());
    Optional<PolicyVersion> version = policy.flatMap(p -> versions.findActiveForEvaluation(p.id()));

    Evaluation evaluation;
    if (policy.isEmpty()) {
      evaluation = Evaluation.reviewRequired(ReasonCodes.NO_APPLICABLE_POLICY);
    } else if (version.isEmpty()) {
      evaluation = Evaluation.reviewRequired(ReasonCodes.NO_ACTIVE_POLICY_VERSION);
    } else {
      evaluation = PolicyEvaluator.evaluate(version.get().rules(), input);
    }

    return new DecisionRecord(
        UUID.randomUUID(),
        transactionId,
        input.organizationId(),
        fingerprint,
        policy.map(Policy::id).orElse(null),
        version.map(PolicyVersion::id).orElse(null),
        version.map(v -> policy.get().versionLabel(v.number())).orElse(NO_VERSION_LABEL),
        evaluation.decision(),
        evaluation.reasonCodes(),
        input.transactionType(),
        input.currency(),
        input.totalAmount(),
        contractVersion,
        correlationId,
        clock.instant().truncatedTo(ChronoUnit.MICROS),
        evaluation.matchedRules().stream()
            .map(r -> new Match(r.position(), r.outcome(), r.reasonCode()))
            .toList());
  }

  private DecisionOutcome replay(DecisionRecord existing, String fingerprint) {
    if (!existing.requestFingerprint().equals(fingerprint)) {
      log.atWarn()
          .addKeyValue("transactionId", existing.transactionId())
          .addKeyValue("decisionId", existing.id())
          .log("Conflicting duplicate evaluation request rejected");
      throw new IdempotencyConflictException(existing.transactionId(), existing.id());
    }
    logDecision(existing, true);
    return new DecisionOutcome(existing, true);
  }

  private static void logDecision(DecisionRecord d, boolean replayed) {
    log.atInfo()
        .addKeyValue("transactionId", d.transactionId())
        .addKeyValue("decisionId", d.id())
        .addKeyValue("policyId", d.policyId())
        .addKeyValue("policyVersion", d.policyVersionLabel())
        .addKeyValue("decision", d.decision())
        .addKeyValue("reasonCodes", String.join(",", d.reasonCodes()))
        .addKeyValue("replayed", replayed)
        .log("Policy decision");
  }
}
