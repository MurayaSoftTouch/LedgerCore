package io.ledgercore.policy.persistence;

import io.ledgercore.policy.domain.Policy;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.time.Instant;
import java.time.OffsetDateTime;
import java.util.List;
import java.util.Optional;
import java.util.UUID;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Repository;

@Repository
public class PolicyRepository {

  private static final String COLUMNS =
      "id, key, name, description, organization_id, created_at, created_by";

  private final JdbcClient jdbc;

  public PolicyRepository(JdbcClient jdbc) {
    this.jdbc = jdbc;
  }

  public void insert(Policy policy) {
    jdbc.sql(
            "INSERT INTO policies (id, key, name, description, organization_id, created_at, created_by)"
                + " VALUES (:id, :key, :name, :description, :organizationId, :createdAt, :createdBy)")
        .param("id", policy.id())
        .param("key", policy.key())
        .param("name", policy.name())
        .param("description", policy.description())
        .param("organizationId", policy.organizationId())
        .param("createdAt", Timestamps.toDb(policy.createdAt()))
        .param("createdBy", policy.createdBy())
        .update();
  }

  public Optional<Policy> findById(UUID id) {
    return jdbc.sql("SELECT " + COLUMNS + " FROM policies WHERE id = :id")
        .param("id", id)
        .query(PolicyRepository::map)
        .optional();
  }

  /**
   * Row-locks the policy. Every version change for a policy takes this lock first, which serialises
   * version numbering and activation per policy.
   */
  public Optional<Policy> lock(UUID id) {
    return jdbc.sql("SELECT " + COLUMNS + " FROM policies WHERE id = :id FOR UPDATE")
        .param("id", id)
        .query(PolicyRepository::map)
        .optional();
  }

  /** The organization's own policy if it has one, otherwise the default policy. */
  public Optional<Policy> findApplicable(UUID organizationId) {
    return jdbc.sql(
            "SELECT "
                + COLUMNS
                + " FROM policies WHERE organization_id = :org OR organization_id IS NULL"
                + " ORDER BY organization_id NULLS LAST LIMIT 1")
        .param("org", organizationId)
        .query(PolicyRepository::map)
        .optional();
  }

  public List<Policy> findAll() {
    return jdbc.sql("SELECT " + COLUMNS + " FROM policies ORDER BY key")
        .query(PolicyRepository::map)
        .list();
  }

  private static Policy map(ResultSet rs, int row) throws SQLException {
    return new Policy(
        rs.getObject("id", UUID.class),
        rs.getString("key"),
        rs.getString("name"),
        rs.getString("description"),
        rs.getObject("organization_id", UUID.class),
        Timestamps.fromDb(rs, "created_at"),
        rs.getString("created_by"));
  }

  /** timestamptz ↔ Instant via OffsetDateTime (the JDBC-standard mapping). */
  static final class Timestamps {
    private Timestamps() {}

    static OffsetDateTime toDb(Instant instant) {
      return instant == null ? null : instant.atOffset(java.time.ZoneOffset.UTC);
    }

    static Instant fromDb(ResultSet rs, String column) throws SQLException {
      var value = rs.getObject(column, OffsetDateTime.class);
      return value == null ? null : value.toInstant();
    }
  }
}
