#!/usr/bin/env bash
# Runs once, on first initialisation of an empty data volume.
# Each service gets its own database and owning role. PUBLIC CONNECT is revoked,
# so the ledger roles cannot open the policy database and vice versa (ADR-002/003).
#
# Both services separate schema ownership from runtime access (ADR-007):
#   ledger_app / policy_app          own their schema and run migrations (DDL).
#   ledger_runtime / policy_runtime  used by the services at runtime: SELECT/INSERT/UPDATE only.
#                                    No DELETE, TRUNCATE or DDL, and as non-owners they cannot
#                                    disable their database's guard triggers.
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v ledger_password="$LEDGER_DB_PASSWORD" \
  -v ledger_runtime_password="$LEDGER_RUNTIME_DB_PASSWORD" \
  -v policy_password="$POLICY_DB_PASSWORD" \
  -v policy_runtime_password="$POLICY_RUNTIME_DB_PASSWORD" <<'SQL'
CREATE ROLE ledger_app LOGIN PASSWORD :'ledger_password';
CREATE ROLE ledger_runtime LOGIN PASSWORD :'ledger_runtime_password';
CREATE ROLE policy_app LOGIN PASSWORD :'policy_password';
CREATE ROLE policy_runtime LOGIN PASSWORD :'policy_runtime_password';

CREATE DATABASE ledger OWNER ledger_app;
CREATE DATABASE policy OWNER policy_app;

REVOKE ALL ON DATABASE ledger FROM PUBLIC;
REVOKE ALL ON DATABASE policy FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE ledger TO ledger_app;
GRANT CONNECT ON DATABASE ledger TO ledger_runtime;
GRANT CONNECT, TEMPORARY ON DATABASE policy TO policy_app;
GRANT CONNECT ON DATABASE policy TO policy_runtime;
SQL

for db in ledger policy; do
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$db" <<SQL
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO ${db}_app;
SQL
done

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname ledger <<'SQL'
GRANT USAGE ON SCHEMA public TO ledger_runtime;
-- Applies to every table/sequence ledger_app creates from now on (i.e. all migrations).
ALTER DEFAULT PRIVILEGES FOR ROLE ledger_app IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE ON TABLES TO ledger_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE ledger_app IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO ledger_runtime;
SQL

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname policy <<'SQL'
GRANT USAGE ON SCHEMA public TO policy_runtime;
-- Applies to every table/sequence policy_app creates (i.e. all Flyway migrations); Flyway V3
-- then narrows these to exactly what the service needs.
ALTER DEFAULT PRIVILEGES FOR ROLE policy_app IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE ON TABLES TO policy_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE policy_app IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO policy_runtime;
SQL
