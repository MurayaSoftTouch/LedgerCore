package io.ledgercore.policy.web;

import io.ledgercore.policy.domain.AccountType;
import io.ledgercore.policy.domain.Decision;
import io.ledgercore.policy.domain.EntrySide;
import jakarta.validation.Valid;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Pattern;
import jakarta.validation.constraints.Size;
import java.time.Instant;
import java.time.OffsetDateTime;
import java.util.List;
import java.util.UUID;

/**
 * Wire types for contract v1 ({@code contracts/schemas/policy-decision-*.v1.schema.json}). Field
 * names, required-ness and patterns mirror the schemas; unknown fields are rejected by Jackson
 * configuration ({@link JsonStrictness}).
 */
public final class DecisionContract {

  /** Contract version this service implements (see {@code contracts/openapi}). */
  public static final String CONTRACT_VERSION_PATTERN = "^1\\.\\d+\\.\\d+$";

  private DecisionContract() {}

  public record PolicyDecisionRequest(
      @NotNull String contractVersion,
      @NotNull UUID transactionId,
      @NotNull UUID organizationId,
      // Open pattern, not a closed enum: a later 1.x type must be tolerated (ADR-005).
      @NotNull @Pattern(regexp = "^[A-Z][A-Z0-9_]{0,63}$") String transactionType,
      @NotNull @Pattern(regexp = "^[A-Z]{3}$") String currency,
      @NotNull @Pattern(regexp = "^(0|[1-9]\\d{0,17})(\\.\\d{1,4})?$") String totalAmount,
      @NotNull @Size(min = 2, max = 100) List<@NotNull @Valid AccountContextItem> accountContext,
      @NotNull @Valid RequestedBy requestedBy,
      @NotNull OffsetDateTime requestedAt) {}

  public record AccountContextItem(
      @NotNull UUID accountId, @NotNull AccountType accountType, @NotNull EntrySide side) {}

  public record RequestedBy(
      @NotNull @Size(min = 1, max = 128) String principalId,
      @NotNull PrincipalType principalType) {}

  public enum PrincipalType {
    USER,
    SERVICE
  }

  public record PolicyDecisionResponse(
      String contractVersion,
      UUID decisionId,
      UUID transactionId,
      String policyVersion,
      Decision decision,
      List<String> reasonCodes,
      Instant evaluatedAt) {}
}
