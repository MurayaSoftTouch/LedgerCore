#!/usr/bin/env bash
# Validates the ledger-policy contract: OpenAPI lint, JSON Schema compilation,
# and example payloads (*.valid.json must pass, *.invalid.json must fail).
set -euo pipefail

cd "$(dirname "$0")"

REDOCLY="@redocly/cli@2.54.2"
AJV="ajv-cli@5.0.0"
AJV_FORMATS="ajv-formats@3.0.1"

npx --yes "$REDOCLY" lint --config .redocly.yaml openapi/policy-decision.v1.yaml

ajv() {
  npx --yes -p "$AJV" -p "$AJV_FORMATS" ajv "$@" --spec=draft2020 -c ajv-formats --strict=true --all-errors
}

for schema in schemas/*.schema.json; do
  ajv compile -s "$schema"
done

status=0
for example in schemas/examples/*.json; do
  name="$(basename "$example")"
  case "$name" in
    request.*) schema=schemas/policy-decision-request.v1.schema.json ;;
    response.*) schema=schemas/policy-decision-response.v1.schema.json ;;
    *) echo "unrecognised example: $name"; exit 1 ;;
  esac
  if ajv validate -s "$schema" -d "$example" >/dev/null 2>&1; then result=valid; else result=invalid; fi
  expected="${name%.json}"; expected="${expected##*.}"
  if [[ "$result" == "$expected" ]]; then
    echo "ok    $name ($result)"
  else
    echo "FAIL  $name expected $expected, got $result"; status=1
  fi
done
exit "$status"
