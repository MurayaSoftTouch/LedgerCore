package io.ledgercore.policy.web;

import com.fasterxml.jackson.annotation.JsonInclude;
import io.ledgercore.policy.domain.AccountType;
import io.ledgercore.policy.domain.Decision;
import io.ledgercore.policy.domain.EntrySide;
import io.ledgercore.policy.domain.Policy;
import io.ledgercore.policy.domain.PolicyVersion;
import io.ledgercore.policy.domain.RuleOutcome;
import io.ledgercore.policy.domain.TransactionType;
import io.ledgercore.policy.domain.VersionStatus;
import io.ledgercore.policy.domain.rules.AccountContextRule;
import io.ledgercore.policy.domain.rules.AmountAboveRule;
import io.ledgercore.policy.domain.rules.CurrencyNotAllowedRule;
import io.ledgercore.policy.domain.rules.Rule;
import io.ledgercore.policy.domain.rules.RuleType;
import io.ledgercore.policy.domain.rules.TransactionTypeRule;
import io.ledgercore.policy.persistence.DecisionRecord;
import jakarta.validation.Valid;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Size;
import java.time.Instant;
import java.util.List;
import java.util.UUID;

/** Wire types for the management API (not part of contract v1). */
public final class ManagementContract {

  private ManagementContract() {}

  public record CreatePolicyRequest(
      @NotNull String key, @NotNull String name, String description, UUID organizationId) {}

  public record PolicyResponse(
      UUID id,
      String key,
      String name,
      String description,
      UUID organizationId,
      Instant createdAt,
      String createdBy,
      Integer activeVersion) {

    static PolicyResponse from(Policy p, Integer activeVersion) {
      return new PolicyResponse(
          p.id(),
          p.key(),
          p.name(),
          p.description(),
          p.organizationId(),
          p.createdAt(),
          p.createdBy(),
          activeVersion);
    }
  }

  public record CreateVersionRequest(
      @NotNull @Size(max = Rule.MAX_RULES_PER_VERSION) List<@NotNull @Valid RuleRequest> rules) {}

  /**
   * One rule. {@code type} selects which of the remaining fields apply; supplying a field that does
   * not belong to the type is an error rather than being ignored.
   *
   * @param threshold decimal string, e.g. {@code "10000.00"} (AMOUNT_ABOVE)
   */
  public record RuleRequest(
      @NotNull RuleType type,
      @NotNull RuleOutcome outcome,
      @NotNull String reasonCode,
      String currency,
      String threshold,
      List<TransactionType> transactionTypes,
      List<AccountType> accountTypes,
      EntrySide side,
      List<UUID> accountIds,
      List<String> allowedCurrencies) {}

  @JsonInclude(JsonInclude.Include.NON_NULL)
  public record RuleResponse(
      int position,
      RuleType type,
      RuleOutcome outcome,
      String reasonCode,
      String currency,
      String threshold,
      List<TransactionType> transactionTypes,
      List<AccountType> accountTypes,
      EntrySide side,
      List<UUID> accountIds,
      List<String> allowedCurrencies) {

    static RuleResponse from(Rule rule) {
      return switch (rule) {
        case AmountAboveRule r ->
            new RuleResponse(
                r.position(),
                r.type(),
                r.outcome(),
                r.reasonCode(),
                r.currency(),
                r.threshold().toPlainString(),
                null,
                null,
                null,
                null,
                null);
        case TransactionTypeRule r ->
            new RuleResponse(
                r.position(),
                r.type(),
                r.outcome(),
                r.reasonCode(),
                null,
                null,
                List.copyOf(r.transactionTypes()),
                null,
                null,
                null,
                null);
        case AccountContextRule r ->
            new RuleResponse(
                r.position(),
                r.type(),
                r.outcome(),
                r.reasonCode(),
                null,
                null,
                null,
                r.accountTypes().isEmpty() ? null : List.copyOf(r.accountTypes()),
                r.side(),
                r.accountIds().isEmpty() ? null : List.copyOf(r.accountIds()),
                null);
        case CurrencyNotAllowedRule r ->
            new RuleResponse(
                r.position(),
                r.type(),
                r.outcome(),
                r.reasonCode(),
                null,
                null,
                null,
                null,
                null,
                null,
                List.copyOf(r.allowedCurrencies()));
      };
    }
  }

  public record VersionResponse(
      UUID policyId,
      int version,
      String label,
      VersionStatus status,
      List<RuleResponse> rules,
      Instant createdAt,
      String createdBy,
      Instant activatedAt,
      String activatedBy,
      Instant retiredAt,
      String retiredBy) {

    static VersionResponse from(Policy policy, PolicyVersion v) {
      return new VersionResponse(
          v.policyId(),
          v.number(),
          policy.versionLabel(v.number()),
          v.status(),
          v.rules().stream().map(RuleResponse::from).toList(),
          v.createdAt(),
          v.createdBy(),
          v.activatedAt(),
          v.activatedBy(),
          v.retiredAt(),
          v.retiredBy());
    }
  }

  /** A stored decision with everything needed to explain it. */
  public record DecisionDetailResponse(
      UUID decisionId,
      UUID transactionId,
      UUID organizationId,
      UUID policyId,
      String policyVersion,
      Decision decision,
      List<String> reasonCodes,
      List<MatchedRule> matchedRules,
      EvaluatedInputs evaluatedInputs,
      String contractVersion,
      String correlationId,
      Instant evaluatedAt) {

    static DecisionDetailResponse from(DecisionRecord d) {
      return new DecisionDetailResponse(
          d.id(),
          d.transactionId(),
          d.organizationId(),
          d.policyId(),
          d.policyVersionLabel(),
          d.decision(),
          d.reasonCodes(),
          d.matches().stream()
              .map(m -> new MatchedRule(m.position(), m.outcome(), m.reasonCode()))
              .toList(),
          new EvaluatedInputs(d.transactionType(), d.currency(), d.totalAmount().toPlainString()),
          d.contractVersion(),
          d.correlationId(),
          d.evaluatedAt());
    }
  }

  public record MatchedRule(int position, RuleOutcome outcome, String reasonCode) {}

  public record EvaluatedInputs(String transactionType, String currency, String totalAmount) {}
}
