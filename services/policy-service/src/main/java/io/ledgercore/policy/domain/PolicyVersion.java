package io.ledgercore.policy.domain;

import io.ledgercore.policy.domain.rules.Rule;
import java.time.Instant;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;
import java.util.Objects;
import java.util.UUID;

/**
 * One immutable revision of a policy's rules. The rules never change after creation; only the
 * lifecycle moves (ADR-009). Corrections are made by creating the next version.
 *
 * <p>{@code activatedAt} and {@code retiredAt} record the interval during which the version was the
 * one used for new evaluations.
 */
public record PolicyVersion(
    UUID id,
    UUID policyId,
    int number,
    VersionStatus status,
    List<Rule> rules,
    Instant createdAt,
    String createdBy,
    Instant activatedAt,
    String activatedBy,
    Instant retiredAt,
    String retiredBy) {

  public PolicyVersion {
    Objects.requireNonNull(id, "id");
    Objects.requireNonNull(policyId, "policyId");
    Objects.requireNonNull(status, "status");
    rules = rules.stream().sorted(Comparator.comparingInt(Rule::position)).toList();
  }

  public static PolicyVersion draft(
      UUID policyId, int number, List<Rule> rules, String actor, Instant now) {
    if (number < 1) {
      throw new IllegalArgumentException("Version numbers start at 1.");
    }
    if (rules == null || rules.size() > Rule.MAX_RULES_PER_VERSION) {
      throw PolicyDomainException.invalid(
          "RULES_INVALID", "A version holds 0-" + Rule.MAX_RULES_PER_VERSION + " rules.");
    }
    var positions = new HashSet<Integer>();
    for (var rule : rules) {
      if (!positions.add(rule.position())) {
        throw PolicyDomainException.invalid(
            "RULE_POSITION_DUPLICATE", "Rule position " + rule.position() + " is used twice.");
      }
    }
    return new PolicyVersion(
        UUID.randomUUID(),
        policyId,
        number,
        VersionStatus.DRAFT,
        rules,
        now,
        Text.actor(actor),
        null,
        null,
        null,
        null);
  }

  public PolicyVersion activate(String actor, Instant now) {
    var activatedBy = Text.actor(actor);
    require(VersionStatus.DRAFT, "activate");
    return new PolicyVersion(
        id,
        policyId,
        number,
        VersionStatus.ACTIVE,
        rules,
        createdAt,
        createdBy,
        now,
        activatedBy,
        null,
        null);
  }

  /** Retires an ACTIVE version, or withdraws a DRAFT that was never activated. */
  public PolicyVersion retire(String actor, Instant now) {
    var retiredBy = Text.actor(actor);
    if (status == VersionStatus.RETIRED) {
      throw PolicyDomainException.conflict(
          "VERSION_ALREADY_RETIRED", "Version " + number + " is already retired.");
    }
    return new PolicyVersion(
        id,
        policyId,
        number,
        VersionStatus.RETIRED,
        rules,
        createdAt,
        createdBy,
        activatedAt,
        activatedBy,
        now,
        retiredBy);
  }

  private void require(VersionStatus required, String operation) {
    if (status != required) {
      throw PolicyDomainException.conflict(
          "VERSION_INVALID_STATE",
          "Cannot " + operation + " version " + number + " in state " + status + ".");
    }
  }
}
