package io.ledgercore.policy.persistence;

import io.ledgercore.policy.domain.AccountType;
import io.ledgercore.policy.domain.EntrySide;
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
import io.ledgercore.policy.persistence.PolicyRepository.Timestamps;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.util.EnumSet;
import java.util.HashSet;
import java.util.List;
import java.util.Optional;
import java.util.UUID;
import java.util.stream.Collectors;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Repository;

@Repository
public class PolicyVersionRepository {

  private static final String COLUMNS =
      "id, policy_id, version_number, status, created_at, created_by, activated_at, activated_by,"
          + " retired_at, retired_by";

  private final JdbcClient jdbc;

  public PolicyVersionRepository(JdbcClient jdbc) {
    this.jdbc = jdbc;
  }

  /** Caller must hold the policy lock ({@link PolicyRepository#lock}). */
  public int nextNumber(UUID policyId) {
    return jdbc.sql(
            "SELECT coalesce(max(version_number), 0) + 1 FROM policy_versions WHERE policy_id = :p")
        .param("p", policyId)
        .query(Integer.class)
        .single();
  }

  public void insert(PolicyVersion version) {
    jdbc.sql(
            "INSERT INTO policy_versions (id, policy_id, version_number, status, created_at,"
                + " created_by) VALUES (:id, :policyId, :number, :status, :createdAt, :createdBy)")
        .param("id", version.id())
        .param("policyId", version.policyId())
        .param("number", version.number())
        .param("status", version.status().name())
        .param("createdAt", Timestamps.toDb(version.createdAt()))
        .param("createdBy", version.createdBy())
        .update();
    for (var rule : version.rules()) {
      insertRule(version.id(), rule);
    }
  }

  /** Persists a lifecycle transition. The row's content columns are never written again. */
  public void updateLifecycle(PolicyVersion version) {
    jdbc.sql(
            "UPDATE policy_versions SET status = :status, activated_at = :activatedAt,"
                + " activated_by = :activatedBy, retired_at = :retiredAt, retired_by = :retiredBy"
                + " WHERE id = :id")
        .param("id", version.id())
        .param("status", version.status().name())
        .param("activatedAt", Timestamps.toDb(version.activatedAt()))
        .param("activatedBy", version.activatedBy())
        .param("retiredAt", Timestamps.toDb(version.retiredAt()))
        .param("retiredBy", version.retiredBy())
        .update();
  }

  public Optional<PolicyVersion> find(UUID policyId, int number) {
    return jdbc.sql(
            "SELECT "
                + COLUMNS
                + " FROM policy_versions WHERE policy_id = :p AND version_number = :n")
        .param("p", policyId)
        .param("n", number)
        .query(this::map)
        .optional();
  }

  public Optional<PolicyVersion> findActive(UUID policyId) {
    return jdbc.sql(
            "SELECT "
                + COLUMNS
                + " FROM policy_versions WHERE policy_id = :p AND status = 'ACTIVE'")
        .param("p", policyId)
        .query(this::map)
        .optional();
  }

  /**
   * The ACTIVE version, share-locked for the rest of the transaction: an activation or retirement
   * of this version waits until the evaluation using it has committed its decision.
   */
  public Optional<PolicyVersion> findActiveForEvaluation(UUID policyId) {
    return jdbc.sql(
            "SELECT "
                + COLUMNS
                + " FROM policy_versions WHERE policy_id = :p AND status = 'ACTIVE' FOR SHARE")
        .param("p", policyId)
        .query(this::map)
        .optional();
  }

  public List<PolicyVersion> findAll(UUID policyId) {
    return jdbc.sql(
            "SELECT "
                + COLUMNS
                + " FROM policy_versions WHERE policy_id = :p ORDER BY version_number")
        .param("p", policyId)
        .query(this::map)
        .list();
  }

  private void insertRule(UUID versionId, Rule rule) {
    var statement =
        jdbc.sql(
                "INSERT INTO policy_rules (id, policy_version_id, position, rule_type, outcome,"
                    + " reason_code, currency, threshold, transaction_types, account_types,"
                    + " entry_side, account_ids, allowed_currencies) VALUES (:id, :versionId,"
                    + " :position, :type, :outcome, :reasonCode, :currency, :threshold,"
                    + " :transactionTypes, :accountTypes, :side, :accountIds, :allowedCurrencies)")
            .param("id", UUID.randomUUID())
            .param("versionId", versionId)
            .param("position", rule.position())
            .param("type", rule.type().name())
            .param("outcome", rule.outcome().name())
            .param("reasonCode", rule.reasonCode());
    String currency = null;
    java.math.BigDecimal threshold = null;
    String[] transactionTypes = null;
    String[] accountTypes = null;
    String side = null;
    UUID[] accountIds = null;
    String[] allowedCurrencies = null;
    switch (rule) {
      case AmountAboveRule r -> {
        currency = r.currency();
        threshold = r.threshold();
      }
      case TransactionTypeRule r ->
          transactionTypes = SqlArrays.text(r.transactionTypes().stream().map(Enum::name).toList());
      case AccountContextRule r -> {
        accountTypes = SqlArrays.text(r.accountTypes().stream().map(Enum::name).toList());
        side = r.side() == null ? null : r.side().name();
        accountIds = SqlArrays.uuids(List.copyOf(r.accountIds()));
      }
      case CurrencyNotAllowedRule r ->
          allowedCurrencies = SqlArrays.text(List.copyOf(r.allowedCurrencies()));
    }
    statement
        .param("currency", currency)
        .param("threshold", threshold)
        .param("transactionTypes", transactionTypes)
        .param("accountTypes", accountTypes)
        .param("side", side)
        .param("accountIds", accountIds)
        .param("allowedCurrencies", allowedCurrencies)
        .update();
  }

  private List<Rule> rules(UUID versionId) {
    return jdbc.sql(
            "SELECT position, rule_type, outcome, reason_code, currency, threshold,"
                + " transaction_types, account_types, entry_side, account_ids, allowed_currencies"
                + " FROM policy_rules WHERE policy_version_id = :v ORDER BY position")
        .param("v", versionId)
        .query(PolicyVersionRepository::mapRule)
        .list();
  }

  /**
   * Rebuilds a rule through its validating constructor. A stored row that no longer forms a valid
   * rule raises an exception instead of being silently skipped.
   */
  private static Rule mapRule(ResultSet rs, int row) throws SQLException {
    var position = rs.getInt("position");
    var outcome = RuleOutcome.valueOf(rs.getString("outcome"));
    var reasonCode = rs.getString("reason_code");
    return switch (RuleType.valueOf(rs.getString("rule_type"))) {
      case AMOUNT_ABOVE ->
          new AmountAboveRule(
              position,
              outcome,
              reasonCode,
              rs.getString("currency"),
              rs.getBigDecimal("threshold"));
      case TRANSACTION_TYPE ->
          new TransactionTypeRule(
              position,
              outcome,
              reasonCode,
              enumSet(TransactionType.class, SqlArrays.textList(rs, "transaction_types")));
      case ACCOUNT_CONTEXT -> {
        var side = rs.getString("entry_side");
        var ids = SqlArrays.uuidList(rs, "account_ids");
        yield new AccountContextRule(
            position,
            outcome,
            reasonCode,
            enumSet(AccountType.class, SqlArrays.textList(rs, "account_types")),
            side == null ? null : EntrySide.valueOf(side),
            ids == null ? null : new HashSet<>(ids));
      }
      case CURRENCY_NOT_ALLOWED ->
          new CurrencyNotAllowedRule(
              position,
              outcome,
              reasonCode,
              new HashSet<>(SqlArrays.textList(rs, "allowed_currencies")));
    };
  }

  private static <E extends Enum<E>> EnumSet<E> enumSet(Class<E> type, List<String> names) {
    if (names == null) {
      return EnumSet.noneOf(type);
    }
    return names.stream()
        .map(n -> Enum.valueOf(type, n))
        .collect(Collectors.toCollection(() -> EnumSet.noneOf(type)));
  }

  private PolicyVersion map(ResultSet rs, int row) throws SQLException {
    var id = rs.getObject("id", UUID.class);
    return new PolicyVersion(
        id,
        rs.getObject("policy_id", UUID.class),
        rs.getInt("version_number"),
        VersionStatus.valueOf(rs.getString("status")),
        rules(id),
        Timestamps.fromDb(rs, "created_at"),
        rs.getString("created_by"),
        Timestamps.fromDb(rs, "activated_at"),
        rs.getString("activated_by"),
        Timestamps.fromDb(rs, "retired_at"),
        rs.getString("retired_by"));
  }
}
