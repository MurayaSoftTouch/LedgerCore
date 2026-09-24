package io.ledgercore.policy.persistence;

import io.ledgercore.policy.domain.Decision;
import io.ledgercore.policy.domain.RuleOutcome;
import io.ledgercore.policy.persistence.DecisionRecord.Match;
import io.ledgercore.policy.persistence.PolicyRepository.Timestamps;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.util.List;
import java.util.Optional;
import java.util.UUID;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Repository;

@Repository
public class DecisionRepository {

  private static final String COLUMNS =
      "id, transaction_id, organization_id, request_fingerprint, policy_id, policy_version_id,"
          + " policy_version_label, decision, reason_codes, transaction_type, currency,"
          + " total_amount, contract_version, correlation_id, evaluated_at";

  private final JdbcClient jdbc;

  public DecisionRepository(JdbcClient jdbc) {
    this.jdbc = jdbc;
  }

  /**
   * Inserts the decision unless one already exists for its transaction. PostgreSQL's unique index
   * decides the race: a concurrent insert of the same transaction waits, then does nothing.
   *
   * @return {@code true} if this call stored the decision
   */
  public boolean insertIfAbsent(DecisionRecord record) {
    var inserted =
        jdbc.sql(
                "INSERT INTO policy_decisions ("
                    + COLUMNS
                    + ") VALUES (:id, :transactionId, :organizationId, :fingerprint, :policyId,"
                    + " :policyVersionId, :label, :decision, :reasonCodes, :transactionType,"
                    + " :currency, :totalAmount, :contractVersion, :correlationId, :evaluatedAt)"
                    + " ON CONFLICT (transaction_id) DO NOTHING")
            .param("id", record.id())
            .param("transactionId", record.transactionId())
            .param("organizationId", record.organizationId())
            .param("fingerprint", record.requestFingerprint())
            .param("policyId", record.policyId())
            .param("policyVersionId", record.policyVersionId())
            .param("label", record.policyVersionLabel())
            .param("decision", record.decision().name())
            .param("reasonCodes", record.reasonCodes().toArray(String[]::new))
            .param("transactionType", record.transactionType())
            .param("currency", record.currency())
            .param("totalAmount", record.totalAmount())
            .param("contractVersion", record.contractVersion())
            .param("correlationId", record.correlationId())
            .param("evaluatedAt", Timestamps.toDb(record.evaluatedAt()))
            .update();
    if (inserted == 0) {
      return false;
    }
    for (var match : record.matches()) {
      // The rule is identified by (version, position); the composite foreign keys guarantee it
      // belongs to the same version as the decision.
      jdbc.sql(
              "INSERT INTO policy_decision_matches (decision_id, rule_id, policy_version_id,"
                  + " position, outcome, reason_code) SELECT :decisionId, r.id,"
                  + " r.policy_version_id, r.position, r.outcome, r.reason_code FROM policy_rules r"
                  + " WHERE r.policy_version_id = :versionId AND r.position = :position")
          .param("decisionId", record.id())
          .param("versionId", record.policyVersionId())
          .param("position", match.position())
          .update();
    }
    return true;
  }

  public Optional<DecisionRecord> findByTransactionId(UUID transactionId) {
    return jdbc.sql("SELECT " + COLUMNS + " FROM policy_decisions WHERE transaction_id = :t")
        .param("t", transactionId)
        .query(this::map)
        .optional();
  }

  public Optional<DecisionRecord> findById(UUID id) {
    return jdbc.sql("SELECT " + COLUMNS + " FROM policy_decisions WHERE id = :id")
        .param("id", id)
        .query(this::map)
        .optional();
  }

  private List<Match> matches(UUID decisionId) {
    return jdbc.sql(
            "SELECT position, outcome, reason_code FROM policy_decision_matches"
                + " WHERE decision_id = :d ORDER BY position")
        .param("d", decisionId)
        .query(
            (rs, row) ->
                new Match(
                    rs.getInt("position"),
                    RuleOutcome.valueOf(rs.getString("outcome")),
                    rs.getString("reason_code")))
        .list();
  }

  private DecisionRecord map(ResultSet rs, int row) throws SQLException {
    var id = rs.getObject("id", UUID.class);
    return new DecisionRecord(
        id,
        rs.getObject("transaction_id", UUID.class),
        rs.getObject("organization_id", UUID.class),
        rs.getString("request_fingerprint"),
        rs.getObject("policy_id", UUID.class),
        rs.getObject("policy_version_id", UUID.class),
        rs.getString("policy_version_label"),
        Decision.valueOf(rs.getString("decision")),
        SqlArrays.textList(rs, "reason_codes"),
        rs.getString("transaction_type"),
        rs.getString("currency"),
        rs.getBigDecimal("total_amount"),
        rs.getString("contract_version"),
        rs.getString("correlation_id"),
        Timestamps.fromDb(rs, "evaluated_at"),
        matches(id));
  }
}
