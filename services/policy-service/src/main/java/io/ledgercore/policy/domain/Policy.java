package io.ledgercore.policy.domain;

import java.time.Instant;
import java.util.Objects;
import java.util.UUID;
import java.util.regex.Pattern;

/**
 * A named set of approval rules with a stable business key. Immutable once created; behaviour
 * changes through new {@link PolicyVersion}s.
 *
 * @param organizationId the organization this policy governs, or {@code null} for the default
 *     policy used when an organization has none of its own
 */
public record Policy(
    UUID id,
    String key,
    String name,
    String description,
    UUID organizationId,
    Instant createdAt,
    String createdBy) {

  private static final Pattern KEY = Pattern.compile("^[a-z0-9][a-z0-9-]{1,62}$");

  public Policy {
    Objects.requireNonNull(id, "id");
    Objects.requireNonNull(createdAt, "createdAt");
  }

  public static Policy create(
      String key, String name, String description, UUID organizationId, String actor, Instant now) {
    if (key == null || !KEY.matcher(key).matches()) {
      throw PolicyDomainException.invalid(
          "POLICY_KEY_INVALID",
          "key must be 2-63 characters of lowercase letters, digits and '-', e.g."
              + " 'default-limits'.");
    }
    return new Policy(
        UUID.randomUUID(),
        key,
        Text.required(name, "name", 200),
        Text.optional(description, "description", 1000),
        organizationId,
        now,
        Text.actor(actor));
  }

  public boolean isDefault() {
    return organizationId == null;
  }

  /** The contract's opaque {@code policyVersion} value for a version of this policy. */
  public String versionLabel(int versionNumber) {
    return key + "@" + versionNumber;
  }
}
