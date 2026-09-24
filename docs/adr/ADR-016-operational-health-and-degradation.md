# ADR-016 — Operational Health and Degradation Model

- Status: Accepted
- Date: 2026-09-24 (Milestone 5, Ngetich-86)
- Reviews required:
  - @LMichy1: ledger readiness and error mapping;
  - @MurayaSoftTouch: policy connection timeout and error handling.

## Context

Milestone 3 gave the ledger three health endpoints: `live`, `ready` (database reachable) and `dependencies` (policy reachable). Milestone 5 had to decide what each one means precisely, and how both services behave when a dependency fails. Three gaps drove the decision:

- **Readiness ignored the schema.** A reachable but unmigrated database made the ledger report ready. The guards live in that schema, and they are part of correctness.
- **A database outage surfaced as a 500** with no code. Clients couldn't tell it from a bug, and couldn't know that nothing had changed.
- **The policy service waited 30 s for a connection** (Hikari's default). During a database outage, every request and readiness probe hung for that long.

## Decision

**Three separate questions**, and none of them is an end-to-end transaction:

| Endpoint | Question | Ledger | Policy |
| --- | --- | --- | --- |
| liveness | Is the process running? | `/health/live`, no checks | `/actuator/health/liveness` |
| readiness | Can it serve its own requests correctly? | `/health/ready`: database reachable **and** every migration this build knows is applied | `/actuator/health/readiness`: readiness state + `db` + `policySchema` |
| dependency | Is something it relies on degraded? | `/health/dependencies`: the policy service's readiness | — |

- **A policy outage is not ledger unreadiness.**
  - Reads, drafting, and posting of already-approved journals don't need the policy service, which reports `Degraded`.
  - Taking the ledger out of rotation would turn a partial outage into a total one.
- **Schema state is part of readiness.** A partly migrated database is `Unhealthy`. A database *ahead of* the build (a rolling deployment) is `Degraded`, which stays ready.
- **The policy service needs no schema-version check at runtime.** Flyway validates at startup (`validate-on-migrate`), and `policySchema` checks that the tables the evaluation path needs are there.

**Behaviour per failure:**

| Failure | Ledger | Policy | Financial state |
| --- | --- | --- | --- |
| policy unreachable | submit → `PENDING_APPROVAL` + `approvalFailure: UNAVAILABLE`; `request-approval` → `503 POLICY_UNAVAILABLE`; dependencies `Degraded`; still ready | — | unchanged; approved journals still post from durable evidence (ADR-015) |
| policy slow (attempt timeout) | bounded by the total budget → `TIMEOUT`; same as above | — | unchanged |
| policy contract violation | `CONTRACT_VIOLATION`, `502` on `request-approval` | — | unchanged, fail closed |
| ledger database unreachable, refusing credentials, or pool exhausted | `503 LEDGER_DATABASE_UNAVAILABLE`, `Retry-After: 1`; unready; live | — | nothing written |
| ledger schema missing or behind | unready | — | the service is taken out of rotation |
| policy database unreachable | the ledger sees policy `503` → `UNAVAILABLE` | `503 POLICY_EVALUATION_UNAVAILABLE`; readiness `DOWN` within about 2 s; live | no decision recorded |
| unexpected exception | `500 INTERNAL_ERROR`, no internals, logged with the correlation id | same | unchanged; each command is one transaction |

**Failing fast is the rule.** The policy service's Hikari `connection-timeout` is 2000 ms (`POLICY_DB_CONNECTION_TIMEOUT_MS`), and the ledger's Npgsql uses its default connect timeout. A database outage becomes a quick `503`, not a hung request.

**Outbox states describe waiting, not delivery.** `/ops/outbox` reports `EMPTY`, `PENDING` or `AGING`: `AGING` means older than `Ledger:OutboxAgingThresholdSeconds`, 300 by default. There is no publisher (ADR-014), so nothing is ever reported as failed delivery.

## Alternatives

- **Ledger readiness includes the policy service.** This was rejected: it makes the ledger unavailable for work that doesn't need policy.
- **Readiness runs a business transaction** (create, post, reverse). This was rejected: it's slow, writes data, and turns the probe into a load generator.
- **Treat a database outage as `500`.** This was rejected: `503` with `Retry-After` tells the client to retry, and that nothing changed.
- **An outbox "failed" state.** This was rejected until a relay exists that can actually fail.

## Consequences

- Deployments must migrate before the new ledger build can become ready. The migration bundle is already a one-shot job that runs first (Compose, CI).
- A policy outage is visible only on `/health/dependencies` and in the logs, not in readiness. Alerting must watch that endpoint.
- Probes are cheap: one connection, one metadata query.

## Known limitations

- There is no metrics backend. Health is exposed only as endpoints, and timings only as structured logs.
- `/ops/outbox` and the reconciliation endpoint are unauthenticated, like the rest of the ledger API (see SECURITY.md). They expose aggregates and ids, never payloads or credentials.
- Readiness doesn't detect a schema that was altered by hand without a migration.
