package io.ledgercore.policy.support;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.catchThrowableOfType;

import java.sql.Connection;
import java.sql.SQLException;
import java.time.Duration;
import java.time.Instant;
import org.postgresql.util.PSQLException;

/** Raw SQL as policy_app, for tests that bypass the service. */
public final class Sql {

  /** SQLSTATE raised by the policy guard triggers. */
  public static final String POLICY_GUARD = "PC001";

  private Sql() {}

  public static long count(String sql, Object... args) throws SQLException {
    try (var c = PolicyPostgres.connect();
        var s = c.prepareStatement(sql)) {
      for (int i = 0; i < args.length; i++) {
        s.setObject(i + 1, args[i]);
      }
      try (var rs = s.executeQuery()) {
        rs.next();
        return rs.getLong(1);
      }
    }
  }

  public static void execute(Connection c, String sql, Object... args) throws SQLException {
    try (var s = c.prepareStatement(sql)) {
      for (int i = 0; i < args.length; i++) {
        s.setObject(i + 1, args[i]);
      }
      s.execute();
    }
  }

  /** Runs the statement in its own connection and returns the PostgreSQL error it must raise. */
  public static PSQLException fails(String sql, Object... args) {
    var error =
        catchThrowableOfType(
            PSQLException.class,
            () -> {
              try (var c = PolicyPostgres.connect()) {
                execute(c, sql, args);
              }
            });
    assertThat((Throwable) error).as("expected a PostgreSQL error from: %s", sql).isNotNull();
    return error;
  }

  public static void assertGuard(PSQLException error, String code) {
    assertThat(error.getSQLState()).isEqualTo(POLICY_GUARD);
    assertThat(error.getServerErrorMessage().getMessage()).startsWith(code + ":");
  }

  /** Condition-based wait (no fixed sleep) until a backend is blocked on a lock. */
  public static void awaitBlockedBackend() throws Exception {
    var deadline = Instant.now().plus(Duration.ofSeconds(30));
    while (count("SELECT count(DISTINCT pid) FROM pg_locks WHERE NOT granted") < 1) {
      if (Instant.now().isAfter(deadline)) {
        throw new AssertionError("no backend became blocked on a lock");
      }
      Thread.sleep(20);
    }
  }
}
