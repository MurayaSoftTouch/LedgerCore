# ADR-005 — Versioned Ledger-Policy Contract

- Status: Accepted
- Date: 2026-09-23

## Context

The ledger needs a policy decision before a journal can be approved. The two services are built in different languages by different contributors. If the contract is left implicit, it will drift, and in a financial system drift must never look like approval.

## Decision

**Artefacts** (source of truth in `contracts/`):

- `contracts/openapi/policy-decision.v1.yaml`: OpenAPI 3.1 description of `POST /v1/policy-decisions`.
- `contracts/schemas/policy-decision-request.v1.schema.json` and `policy-decision-response.v1.schema.json`: JSON Schema 2020-12, referenced by the OpenAPI document.
- `contracts/schemas/examples/`: valid and deliberately invalid payloads; `contracts/validate.sh` asserts each one behaves as its name says.

**Minimal request.** The request carries only what policy evaluation needs: transaction id and type, organization, currency, total amount (decimal string), the accounts touched (id, type, side) and the requesting principal. Balances and per-line amounts are excluded, and `additionalProperties: false` makes a leaked field a schema violation.

**Versioning.**

- The major version goes in the path (`/v1`) and in the schema file name.
- Both messages carry `contractVersion` (semver, pattern-locked to `1.x.y`).
- Minor and patch changes are additive only. New enum values are a minor change; receivers must treat an unknown request `transactionType` as `REVIEW_REQUIRED`.
- A breaking change means a `/v2` path and new schema files, with both served during migration.

**Failure behaviour: the ledger fails closed.** Only one outcome lets a journal move to `APPROVED`: an HTTP `200` whose body validates against the response schema, whose `transactionId` matches, and whose `decision` is `APPROVED`. Everything else is classified and recorded:

| Outcome | Detection | Journal effect |
| --- | --- | --- |
| Explicit rejection | `200`, `decision: REJECTED` | `REJECTED` (terminal) |
| Manual review | `200`, `decision: REVIEW_REQUIRED` | stays `PENDING_APPROVAL`, flagged for review |
| Timeout | no response within `Ledger:PolicyDecisionTimeoutMs` (default 2000 ms, validated 100–30000 at startup) | stays `PENDING_APPROVAL`, retryable |
| Policy-service failure | `5xx`, including `503` "cannot evaluate" | stays `PENDING_APPROVAL`, retryable |
| Contract incompatibility | `409`, `4xx`, schema-invalid body, unknown `decision`, mismatched `transactionId` | stays `PENDING_APPROVAL`, alert raised; never retried blindly |

Timeouts and failures are never turned into a rejection, because the business never said no. They are never turned into an approval either, because nobody said yes.

**Idempotency.** `transactionId` is stable across retries. The policy service should return the original decision when it is re-asked about the same transaction under the same policy version. Its exact semantics are Milestone 2 scope.

## Alternatives

- **Code-first contract** (generate OpenAPI from Spring annotations). Convenient for the server, but it makes the Java implementation the de facto contract, which the .NET side can only discover after the fact.
- **gRPC/Protobuf.** Strong typing and codegen, but adds a toolchain to both sides and is harder to inspect by hand. JSON over HTTP is enough at this volume.
- **Asynchronous decisions over a broker.** Decouples availability, but introduces Kafka-class infrastructure (explicitly out of scope) and makes "is this approved yet?" harder to reason about.
- **Fail open on timeout** for small amounts. Rejected: a policy outage would then silently widen what gets approved.

## Consequences

- A policy-service outage halts new approvals. That is intended; the operational mitigation is availability and retry, not bypass.
- Every schema change goes through `contracts/validate.sh` and review by both service owners.
- Contract tests (Milestone 3) will run both implementations against the same schema and example payloads.

## Known limitations

- Authentication between services is not yet specified; it is added with the policy client in Milestone 3.
- The ledger-side timeout is configured, but no client exists yet.
- Neither service yet validates live traffic against the schemas.
