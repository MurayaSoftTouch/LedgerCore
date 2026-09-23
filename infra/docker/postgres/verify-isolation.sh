#!/usr/bin/env bash
# Asserts each application role can use its own database and cannot connect to the other's.
# Reads passwords from .env (local defaults in .env.example). Requires `docker compose up`.
set -euo pipefail
cd "$(dirname "$0")/../../.."
set -a; source .env; set +a

run() { # role db password sql
  docker compose exec -T -e PGPASSWORD="$3" postgres \
    psql -h 127.0.0.1 -U "$1" -d "$2" -v ON_ERROR_STOP=1 -tAc "$4" >/dev/null 2>&1
}

status=0
expect() { # description expected(ok|denied) role db password
  if run "$3" "$4" "$5" "CREATE TABLE isolation_probe(id int); DROP TABLE isolation_probe;"; then got=ok; else got=denied; fi
  if [[ "$got" == "$2" ]]; then echo "ok    $1 ($got)"; else echo "FAIL  $1: expected $2, got $got"; status=1; fi
}

expect "ledger_app uses ledger"  ok     ledger_app ledger "$LEDGER_DB_PASSWORD"
expect "policy_app uses policy"  ok     policy_app policy "$POLICY_DB_PASSWORD"
expect "ledger_app -> policy"    denied ledger_app policy "$LEDGER_DB_PASSWORD"
expect "policy_app -> ledger"    denied policy_app ledger "$POLICY_DB_PASSWORD"
exit "$status"
