#!/usr/bin/env bash
# Runs once, on first initialisation of an empty data volume.
# Each service gets its own database and owning role. PUBLIC CONNECT is revoked,
# so the ledger role cannot open the policy database and vice versa (ADR-002/003).
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v ledger_password="$LEDGER_DB_PASSWORD" \
  -v policy_password="$POLICY_DB_PASSWORD" <<'SQL'
CREATE ROLE ledger_app LOGIN PASSWORD :'ledger_password';
CREATE ROLE policy_app LOGIN PASSWORD :'policy_password';

CREATE DATABASE ledger OWNER ledger_app;
CREATE DATABASE policy OWNER policy_app;

REVOKE ALL ON DATABASE ledger FROM PUBLIC;
REVOKE ALL ON DATABASE policy FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE ledger TO ledger_app;
GRANT CONNECT, TEMPORARY ON DATABASE policy TO policy_app;
SQL

for db in ledger policy; do
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$db" <<SQL
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO ${db}_app;
SQL
done
