package io.ledgercore.policy.domain;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import java.time.Instant;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

class PolicyTest {

  @Test
  void createsDefaultPolicyWithLabelledVersions() {
    var policy =
        Policy.create("default-limits", "Default limits", null, null, "admin", Instant.now());

    assertThat(policy.isDefault()).isTrue();
    assertThat(policy.versionLabel(3)).isEqualTo("default-limits@3");
  }

  @ParameterizedTest
  @ValueSource(strings = {"", "a", "Upper", "has space", "-leading", "has@at"})
  void rejectsInvalidKeys(String key) {
    assertThatThrownBy(() -> Policy.create(key, "n", null, null, "admin", Instant.now()))
        .isInstanceOfSatisfying(
            PolicyDomainException.class, e -> assertThat(e.code()).isEqualTo("POLICY_KEY_INVALID"));
  }
}
