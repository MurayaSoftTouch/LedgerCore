#!/usr/bin/env bash
# Asserts the database role boundaries (ADR-002, ADR-003, ADR-007):
#   - each service role can use its own database and cannot connect to the other's;
#   - ledger_runtime / policy_runtime can connect to their own database but cannot run DDL,
#     and cannot connect to the other service's database.
# Reads passwords from .env (local defaults in .env.example). Requires `docker compose up`.
set -euo pipefail
cd "$(dirname "$0")/../../.."
set -a; source .env; set +a

run() { # role db password sql
  docker compose exec -T -e PGPASSWORD="$3" postgres \
    psql -h 127.0.0.1 -U "$1" -d "$2" -v ON_ERROR_STOP=1 -tAc "$4" >/dev/null 2>&1
}

CONNECT="SELECT 1"
DDL="CREATE TABLE isolation_probe(id int); DROP TABLE isolation_probe;"

status=0
expect() { # description expected(ok|denied) role db password sql
  if run "$3" "$4" "$5" "$6"; then got=ok; else got=denied; fi
  if [[ "$got" == "$2" ]]; then echo "ok    $1 ($got)"; else echo "FAIL  $1: expected $2, got $got"; status=1; fi
}

expect "ledger_app DDL on ledger"          ok     ledger_app     ledger "$LEDGER_DB_PASSWORD"         "$DDL"
expect "policy_app DDL on policy"          ok     policy_app     policy "$POLICY_DB_PASSWORD"         "$DDL"
expect "ledger_runtime connects to ledger" ok     ledger_runtime ledger "$LEDGER_RUNTIME_DB_PASSWORD" "$CONNECT"
expect "ledger_runtime DDL on ledger"      denied ledger_runtime ledger "$LEDGER_RUNTIME_DB_PASSWORD" "$DDL"
expect "policy_runtime connects to policy" ok     policy_runtime policy "$POLICY_RUNTIME_DB_PASSWORD" "$CONNECT"
expect "policy_runtime DDL on policy"      denied policy_runtime policy "$POLICY_RUNTIME_DB_PASSWORD" "$DDL"
expect "ledger_app -> policy"              denied ledger_app     policy "$LEDGER_DB_PASSWORD"         "$CONNECT"
expect "ledger_runtime -> policy"          denied ledger_runtime policy "$LEDGER_RUNTIME_DB_PASSWORD" "$CONNECT"
expect "policy_app -> ledger"              denied policy_app     ledger "$POLICY_DB_PASSWORD"         "$CONNECT"
expect "policy_runtime -> ledger"          denied policy_runtime ledger "$POLICY_RUNTIME_DB_PASSWORD" "$CONNECT"
exit "$status"
