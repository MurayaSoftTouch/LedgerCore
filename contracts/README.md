# Contracts

The contract between `ledger-api` (client) and `policy-service` (server). It is the only coupling between the two services.

| File | Purpose |
| --- | --- |
| `openapi/policy-decision.v1.yaml` | OpenAPI 3.1 description of `POST /v1/policy-decisions` |
| `schemas/policy-decision-request.v1.schema.json` | JSON Schema 2020-12 for the request |
| `schemas/policy-decision-response.v1.schema.json` | JSON Schema 2020-12 for the response |
| `schemas/examples/*.valid.json` / `*.invalid.json` | Payloads that must pass or fail validation respectively |
| `validate.sh` | Lints OpenAPI (Redocly), compiles the schemas (Ajv, strict), checks every example |

```bash
./contracts/validate.sh   # requires Node.js 20+; tools are fetched at pinned versions via npx
```

## Rules

- Semantic versioning. Additive changes are a minor bump; breaking changes need a new major (`/v2`, new files).
- Money is a decimal **string** (`"125000.50"`), never a JSON number.
- `additionalProperties: false` everywhere, so leaking a balance or internal id fails validation (see `request.balance-leak.invalid.json`).
- Failure behaviour: the ledger fails closed. See ADR-005 for the outcome table.
- Changes need review from both service owners and the contract owner (`CODEOWNERS`).
