package io.ledgercore.policy.support;

import java.nio.file.Files;
import java.nio.file.Path;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.SQLException;
import org.testcontainers.postgresql.PostgreSQLContainer;
import org.testcontainers.utility.MountableFile;

/**
 * One PostgreSQL 18.6 container for the whole test run, initialised by the repository's real init
 * script (infra/docker/postgres/init), so the service connects exactly as in Compose: as {@code
 * policy_runtime} to the {@code policy} database, with Flyway migrating as {@code policy_app}.
 */
public final class PolicyPostgres {

  /** Schema owner: Flyway only. */
  public static final String POLICY_PASSWORD = "test-policy";

  /** Runtime role: what the service connects as. */
  public static final String POLICY_RUNTIME_PASSWORD = "test-policy-runtime";

  private static final PostgreSQLContainer CONTAINER = start();

  private PolicyPostgres() {}

  public static String jdbcUrl() {
    return "jdbc:postgresql://"
        + CONTAINER.getHost()
        + ":"
        + CONTAINER.getMappedPort(5432)
        + "/policy";
  }

  /**
   * A raw connection as policy_app (the schema owner), for tests proving a trigger or constraint
   * holds even without privilege restrictions.
   */
  public static Connection connect() throws SQLException {
    return DriverManager.getConnection(jdbcUrl(), "policy_app", POLICY_PASSWORD);
  }

  /** A raw connection as policy_runtime, the role the service runs as. */
  public static Connection connectRuntime() throws SQLException {
    return DriverManager.getConnection(jdbcUrl(), "policy_runtime", POLICY_RUNTIME_PASSWORD);
  }

  private static PostgreSQLContainer start() {
    var container =
        new PostgreSQLContainer("postgres:18.6-alpine")
            .withUsername("ledgercore_admin")
            .withPassword("test-admin")
            .withDatabaseName("postgres")
            .withEnv("LEDGER_DB_PASSWORD", "test-ledger")
            .withEnv("LEDGER_RUNTIME_DB_PASSWORD", "test-ledger-runtime")
            .withEnv("POLICY_DB_PASSWORD", POLICY_PASSWORD)
            .withEnv("POLICY_RUNTIME_DB_PASSWORD", POLICY_RUNTIME_PASSWORD)
            .withCopyFileToContainer(
                MountableFile.forHostPath(initScript()),
                "/docker-entrypoint-initdb.d/01-create-databases.sh");
    container.start();
    return container;
  }

  private static Path initScript() {
    for (var dir = Path.of("").toAbsolutePath(); dir != null; dir = dir.getParent()) {
      var candidate = dir.resolve("infra/docker/postgres/init/01-create-databases.sh");
      if (Files.exists(candidate)) {
        return candidate;
      }
    }
    throw new IllegalStateException("infra/docker/postgres/init/01-create-databases.sh not found");
  }
}
