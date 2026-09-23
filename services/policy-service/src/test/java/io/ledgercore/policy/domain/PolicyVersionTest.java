package io.ledgercore.policy.domain;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import io.ledgercore.policy.domain.rules.AmountAboveRule;
import io.ledgercore.policy.domain.rules.Rule;
import java.math.BigDecimal;
import java.time.Instant;
import java.util.List;
import java.util.UUID;
import org.junit.jupiter.api.Test;

class PolicyVersionTest {

  private static final Instant NOW = Instant.parse("2026-09-23T09:00:00Z");

  private static Rule rule(int position) {
    return new AmountAboveRule(position, RuleOutcome.REJECTED, "LIMIT", "KES", BigDecimal.TEN);
  }

  @Test
  void lifecycleDraftActiveRetired() {
    var draft = PolicyVersion.draft(UUID.randomUUID(), 1, List.of(rule(1)), "author", NOW);
    var active = draft.activate("approver", NOW.plusSeconds(60));
    var retired = active.retire("approver", NOW.plusSeconds(120));

    assertThat(draft.status()).isEqualTo(VersionStatus.DRAFT);
    assertThat(active.status()).isEqualTo(VersionStatus.ACTIVE);
    assertThat(active.activatedAt()).isEqualTo(NOW.plusSeconds(60));
    assertThat(retired.status()).isEqualTo(VersionStatus.RETIRED);
    assertThat(retired.activatedAt()).isEqualTo(active.activatedAt());
    assertThat(retired.rules()).isEqualTo(draft.rules());
  }

  @Test
  void retiredIsTerminal() {
    var retired =
        PolicyVersion.draft(UUID.randomUUID(), 1, List.of(), "a", NOW)
            .activate("a", NOW)
            .retire("a", NOW);

    assertThatThrownBy(() -> retired.activate("a", NOW))
        .isInstanceOfSatisfying(
            PolicyDomainException.class,
            e -> assertThat(e.code()).isEqualTo("VERSION_INVALID_STATE"));
    assertThatThrownBy(() -> retired.retire("a", NOW))
        .isInstanceOfSatisfying(
            PolicyDomainException.class,
            e -> assertThat(e.code()).isEqualTo("VERSION_ALREADY_RETIRED"));
  }

  @Test
  void activeCannotBeActivatedAgain() {
    var active = PolicyVersion.draft(UUID.randomUUID(), 1, List.of(), "a", NOW).activate("a", NOW);

    assertThatThrownBy(() -> active.activate("a", NOW)).isInstanceOf(PolicyDomainException.class);
  }

  @Test
  void draftCanBeWithdrawnWithoutEverBeingActive() {
    var withdrawn = PolicyVersion.draft(UUID.randomUUID(), 1, List.of(), "a", NOW).retire("a", NOW);

    assertThat(withdrawn.status()).isEqualTo(VersionStatus.RETIRED);
    assertThat(withdrawn.activatedAt()).isNull();
  }

  @Test
  void duplicateRulePositionsAreRejected() {
    assertThatThrownBy(
            () -> PolicyVersion.draft(UUID.randomUUID(), 1, List.of(rule(1), rule(1)), "a", NOW))
        .isInstanceOfSatisfying(
            PolicyDomainException.class,
            e -> assertThat(e.code()).isEqualTo("RULE_POSITION_DUPLICATE"));
  }

  @Test
  void rulesAreHeldInPositionOrder() {
    var version =
        PolicyVersion.draft(UUID.randomUUID(), 1, List.of(rule(3), rule(1), rule(2)), "a", NOW);

    assertThat(version.rules()).extracting(Rule::position).containsExactly(1, 2, 3);
  }

  @Test
  void rulesListIsImmutable() {
    var version = PolicyVersion.draft(UUID.randomUUID(), 1, List.of(rule(1)), "a", NOW);

    assertThatThrownBy(() -> version.rules().add(rule(2)))
        .isInstanceOf(UnsupportedOperationException.class);
  }

  @Test
  void actorIsRequiredForTransitions() {
    var draft = PolicyVersion.draft(UUID.randomUUID(), 1, List.of(), "a", NOW);

    assertThatThrownBy(() -> draft.activate(" ", NOW))
        .isInstanceOfSatisfying(
            PolicyDomainException.class, e -> assertThat(e.code()).isEqualTo("FIELD_REQUIRED"));
  }
}
