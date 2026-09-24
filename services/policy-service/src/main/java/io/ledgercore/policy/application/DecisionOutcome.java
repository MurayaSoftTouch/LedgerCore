package io.ledgercore.policy.application;

import io.ledgercore.policy.persistence.DecisionRecord;

/**
 * @param replayed {@code true} when an identical request for the transaction was already decided
 *     and the stored decision is returned unchanged
 */
public record DecisionOutcome(DecisionRecord decision, boolean replayed) {}
