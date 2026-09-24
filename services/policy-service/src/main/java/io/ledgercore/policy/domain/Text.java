package io.ledgercore.policy.domain;

/** Input validation shared by the domain types. */
final class Text {

  private Text() {}

  static String required(String value, String field, int maxLength) {
    var trimmed = value == null ? "" : value.strip();
    if (trimmed.isEmpty()) {
      throw PolicyDomainException.invalid("FIELD_REQUIRED", field + " is required.");
    }
    if (trimmed.length() > maxLength) {
      throw PolicyDomainException.invalid(
          "FIELD_TOO_LONG", field + " must be at most " + maxLength + " characters.");
    }
    return trimmed;
  }

  static String optional(String value, String field, int maxLength) {
    return value == null || value.isBlank() ? null : required(value, field, maxLength);
  }

  static String actor(String actor) {
    return required(actor, "actor", 128);
  }
}
