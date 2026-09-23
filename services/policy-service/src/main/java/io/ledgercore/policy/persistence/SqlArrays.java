package io.ledgercore.policy.persistence;

import java.sql.Array;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.util.Arrays;
import java.util.List;
import java.util.UUID;

/** PostgreSQL array helpers. The driver binds Java arrays to {@code text[]} / {@code uuid[]}. */
final class SqlArrays {

  private SqlArrays() {}

  static String[] text(List<String> values) {
    return values == null || values.isEmpty() ? null : values.toArray(String[]::new);
  }

  static UUID[] uuids(List<UUID> values) {
    return values == null || values.isEmpty() ? null : values.toArray(UUID[]::new);
  }

  static List<String> textList(ResultSet rs, String column) throws SQLException {
    Array array = rs.getArray(column);
    return array == null ? null : Arrays.asList((String[]) array.getArray());
  }

  static List<UUID> uuidList(ResultSet rs, String column) throws SQLException {
    Array array = rs.getArray(column);
    return array == null ? null : Arrays.asList((UUID[]) array.getArray());
  }
}
