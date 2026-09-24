#!/usr/bin/env bash
# End-to-end smoke test of the Docker Compose stack (Milestone 3): all services healthy, then one
# journal approved by the real policy service over Compose DNS (ledger-api -> policy-service),
# posted, and its JournalPosted outbox event present.
#
#   ./tests/integration/compose-smoke.sh          # uses an already running stack, or starts one
#   ./tests/integration/compose-smoke.sh --build  # rebuild images first
#
# Needs .env (copy .env.example) with POLICY_ADMIN_API_TOKEN set, plus curl and python3.
set -euo pipefail
cd "$(dirname "$0")/../.."
set -a; source .env; set +a

if [[ "${1:-}" == "--build" ]]; then
  docker compose up -d --build --wait --wait-timeout 600
else
  docker compose up -d --wait --wait-timeout 600
fi

LEDGER="http://127.0.0.1:${LEDGER_PORT:-8080}"
POLICY="http://127.0.0.1:${POLICY_PORT:-8081}"
json() { python3 -c "import sys,json; d=json.load(sys.stdin); print($1)"; }
fail() { echo "FAIL  $*" >&2; exit 1; }
ok() { echo "ok    $*"; }

for url in "$LEDGER/health/ready" "$LEDGER/health/dependencies" "$POLICY/actuator/health/readiness"; do
  body=$(curl -fsS "$url") || fail "$url"
  ok "$url -> $body"
done

H=(-H 'Content-Type: application/json' -H 'X-Actor-Id: compose-smoke')
A=(-H "Authorization: Bearer ${POLICY_ADMIN_API_TOKEN:?set POLICY_ADMIN_API_TOKEN in .env}")
run=$(date +%s)

ledger=$(curl -fsS "${H[@]}" -d "{\"code\":\"SMOKE-$run\",\"name\":\"Compose smoke\"}" "$LEDGER/api/v1/ledgers" | json 'd["id"]')
expense=$(curl -fsS "${H[@]}" -d '{"code":"5000","name":"Expenses","type":"EXPENSE","currency":"KES"}' "$LEDGER/api/v1/ledgers/$ledger/accounts" | json 'd["id"]')
cash=$(curl -fsS "${H[@]}" -d '{"code":"1000","name":"Cash","type":"ASSET","currency":"KES"}' "$LEDGER/api/v1/ledgers/$ledger/accounts" | json 'd["id"]')
ok "ledger $ledger"

policy=$(curl -fsS "${H[@]}" "${A[@]}" -d "{\"key\":\"smoke-$run\",\"name\":\"Smoke\",\"organizationId\":\"$ledger\"}" "$POLICY/api/v1/policies" | json 'd["id"]')
curl -fsS "${H[@]}" "${A[@]}" -d '{"rules":[{"type":"AMOUNT_ABOVE","currency":"KES","threshold":"10000.00","outcome":"REVIEW_REQUIRED","reasonCode":"AMOUNT_EXCEEDS_REVIEW_THRESHOLD"}]}' "$POLICY/api/v1/policies/$policy/versions" >/dev/null
curl -fsS -X POST "${H[@]}" "${A[@]}" "$POLICY/api/v1/policies/$policy/versions/1/activate" >/dev/null
ok "policy smoke-$run@1 active"

journal=$(curl -fsS "${H[@]}" -d '{"currency":"KES","description":"Compose smoke","transactionType":"PAYMENT"}' "$LEDGER/api/v1/ledgers/$ledger/journals" | json 'd["id"]')
curl -fsS "${H[@]}" -d "{\"accountId\":\"$expense\",\"direction\":\"DEBIT\",\"amount\":\"250.00\"}" "$LEDGER/api/v1/ledgers/$ledger/journals/$journal/entries" >/dev/null
curl -fsS "${H[@]}" -d "{\"accountId\":\"$cash\",\"direction\":\"CREDIT\",\"amount\":\"250.00\"}" "$LEDGER/api/v1/ledgers/$ledger/journals/$journal/entries" >/dev/null

submitted=$(curl -fsS -X POST "${H[@]}" -H "X-Correlation-Id: smoke-$run" "$LEDGER/api/v1/ledgers/$ledger/journals/$journal/submit")
status=$(echo "$submitted" | json 'd["status"]')
version=$(echo "$submitted" | json '(d.get("policyDecision") or {}).get("policyVersion")')
[[ "$status" == "APPROVED" && "$version" == "smoke-$run@1" ]] || fail "submit: $submitted"
ok "submitted -> $status by $version"

posted=$(curl -fsS -X POST "${H[@]}" "$LEDGER/api/v1/ledgers/$ledger/journals/$journal/post" | json 'd["status"]')
[[ "$posted" == "POSTED" ]] || fail "post: $posted"
ok "posted"

events=$(docker compose exec -T postgres psql -U "$POSTGRES_SUPERUSER" -d ledger -tAc \
  "SELECT count(*) FROM outbox_events WHERE aggregate_id = '$journal' AND event_type = 'JournalPosted'")
[[ "$events" == "1" ]] || fail "outbox events: $events"
ok "JournalPosted outbox event recorded"

correlation=$(docker compose exec -T postgres psql -U "$POSTGRES_SUPERUSER" -d policy -tAc \
  "SELECT correlation_id FROM policy_decisions WHERE transaction_id = '$journal'")
[[ "$correlation" == "smoke-$run" ]] || fail "policy decision correlation id: $correlation"
ok "correlation id smoke-$run reached the policy service"
echo "compose smoke passed"
