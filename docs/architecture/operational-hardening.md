# Operational Hardening

This covers Milestone 5: health and degradation, reconciliation operations, outbox visibility, logging and correlation, security boundaries, and incident troubleshooting. The decisions are [ADR-016](../adr/ADR-016-operational-health-and-degradation.md) (health and degradation), [ADR-015](../adr/ADR-015-idempotent-posting-and-reversal.md) (idempotency), [ADR-013](../adr/ADR-013-service-to-service-authentication.md) (service authentication) and [ADR-007](../adr/ADR-007-database-enforced-ledger-invariants.md) (database roles).

## Health

| Endpoint | 200 means | 503 means |
| --- | --- | --- |
| ledger `/health/live` | the process runs | never |
| ledger `/health/ready` | the database is reachable **and** every migration of this build is applied (`Degraded`, still 200, if the database is newer than the build) | take it out of rotation: the database is down, or the schema is missing or behind |
| ledger `/health/dependencies` | the policy service is ready (`Healthy`), or unreachable (`Degraded`, still 200) | never |
| policy `/actuator/health/liveness` | the process runs | never |
| policy `/actuator/health/readiness` | the database is reachable and the policy schema is usable | the database is down (detected within about 2 s) |

A policy outage never makes the ledger unready: reads, drafting and posting of approved journals don't need it (ADR-016).

## Degradation

| Failure | What callers see | What happens to money |
| --- | --- | --- |
| policy service down or slow | `submit` → `PENDING_APPROVAL` with `approvalFailure` (`UNAVAILABLE` / `TIMEOUT`); `request-approval` → `503 POLICY_*` | nothing is approved; **approved journals still post** from the recorded evidence |
| policy response violates the contract | `approvalFailure: CONTRACT_VIOLATION`, `502` on retry | fail closed |
| ledger database down, refusing credentials, or pool exhausted | `503 LEDGER_DATABASE_UNAVAILABLE`, `Retry-After: 1` | nothing written (every command is one transaction) |
| policy database down | the ledger sees policy `503` → `UNAVAILABLE`; the policy returns `503 POLICY_EVALUATION_UNAVAILABLE` | no decision recorded |
| unexpected error | `500 INTERNAL_ERROR`, with no internals in the body | unchanged |

The multi-service suite simulates each of these: policy outage and latency, a contract-violating policy service, PostgreSQL stopped under both services and recovery afterwards, and wrong credentials (`tests/integration`).

## Reconciliation

`GET /api/v1/ledgers/{ledgerId}/reconciliation` recomputes the ledger from **persisted `POSTED` entries only**, with plain SQL, in one read-only `REPEATABLE READ` snapshot. It never uses client totals, balance caches, policy data or outbox payloads as a source of amounts.

- **`status`** is `HEALTHY` or `DISCREPANCY`. A scan that fails is an error response (`503`/`500`) and is never reported as `HEALTHY`.
- **`runId`** appears in the response and in every log line of the run.
- **`discrepancies[]`** each have a `category` plus the affected `journalId`, `accountId` or `currency`. There are at most 100 per category, and `discrepanciesTruncated` flags when more exist.

| Category | Meaning |
| --- | --- |
| `UNBALANCED_JOURNAL` | a posted journal whose debits ≠ credits, or that lacks a debit or a credit |
| `CURRENCY_IMBALANCE` | a currency whose posted debits ≠ credits |
| `REVERSAL_ORIGINAL_NOT_POSTED` | a posted reversal whose original is missing or not posted |
| `REVERSAL_MISMATCH` | a posted reversal that doesn't exactly mirror its original (a missing, extra or altered leg) |
| `UNEXPECTED_ACCOUNT_MOVEMENT` | a posted entry on an account of another ledger or currency, or on no account |
| `MISSING_APPROVAL_EVIDENCE` | a posted journal without an `APPROVED` decision |
| `MISSING_POSTED_EVENT` | a posted journal without its `JournalPosted` event |
| `EVENT_EVIDENCE_MISMATCH` | the event's `policyDecisionId` isn't the journal's decision |
| `ORPHAN_POSTED_EVENT` | a `JournalPosted` event for a journal that isn't posted |
| `MISSING_IDEMPOTENCY_CLAIM` | posted after claims became mandatory, yet no claim |

- **Every category is impossible without bypassing the database guards.** A finding means someone bypassed them: the schema owner disabling triggers, a superuser, or restored or tampered data.
- **Reconciliation never corrects anything.**
- **Legacy journals.** Journals posted before Milestone 4 have no idempotency claim, and their audit row has no key. They are counted in `legacyPostingsWithoutClaim` and are expected history, not a discrepancy. Claims are never fabricated for them, and they stay readable.
- **Cost.** A fixed number of statements (at most 8, including the transaction setup), whatever the ledger's size. The 5,000-journal test issues exactly as many as a one-journal ledger (`ReconciliationDiscrepancyTests`). There is no balance cache: posted entries stay authoritative.
- **Logs.** Each run logs its `ReconciliationRunId`, the ledger, the status, its duration, and each category's count with up to 10 sample ids. The correlation id comes from the request scope. No amounts or payloads are logged.

## Outbox visibility

`GET /ops/outbox` returns aggregates only, never payloads or aggregate ids:

```json
{ "state": "AGING", "agingThresholdSeconds": 300, "pending": 42,
  "oldestPendingAt": "…", "oldestPendingAgeSeconds": 7210,
  "eventTypes": [{ "eventType": "JournalPosted", "pending": 42, "state": "AGING", … }],
  "publisherConfigured": false }
```

- The states are `EMPTY`, `PENDING` and `AGING` (older than `Ledger:OutboxAgingThresholdSeconds`).
- There is no publisher yet (ADR-014). Until one exists, every event eventually becomes `AGING`. That is expected, and it isn't a delivery failure.

## Logging

Both services log structured JSON: the ledger through the .NET JSON console, the policy service in ECS format.

**Ledger log events:**

| Event | Fields |
| --- | --- |
| every request | `HttpMethod`, `Route` (the template, not the raw path), `StatusCode`, `DurationMs`, and a `CorrelationId` scope. Health probes are logged at Debug |
| post / reverse | `Operation`, `LedgerId`, `JournalId`, `ResultJournalId`, `Outcome` (`COMPLETED` / `REPLAYED` / the refusal code), `IdempotencyKeyRef`, `DurationMs` |
| policy call | `PolicyTransactionId`, `PolicyDecisionId`, `Decision` or `FailureKind`, and `DurationMs` including retries |
| policy decision recorded | `JournalId`, `PolicyDecisionId`, `PolicyVersion`, `Decision` |
| reconciliation | `ReconciliationRunId`, `LedgerId`, the status, per-category counts, sample ids, `DurationMs` |
| database unavailable / unexpected | the exception type or the exception, with the correlation id |

**Policy log events:** the decision (`transactionId`, `decisionId`, `policyVersion`, `decision`, `reasonCodes`, `correlationId`), authentication rejections (path and code), and unexpected errors.

**Never logged, and proven by tests:**
- bearer credentials, including inbound `Authorization` headers;
- database passwords and connection strings;
- `.env` values;
- request bodies and query strings;
- raw idempotency keys: logs show `IdempotencyKeyRef`, the first 12 hex digits of the key's SHA-256.

The tests that prove this are:
- ledger `SecretRedactionTests`, which cover successful and failing requests, the real policy client, and a database with a wrong password;
- policy `HttpHardeningTests` and `ServiceAuthenticationTests`;
- the multi-service `CredentialsNeverAppearInEitherServiceLog`, which checks all 5 database passwords and both tokens.

**Correlation:**
- A valid inbound `X-Correlation-Id` is kept; a missing or malformed one is replaced.
- The id is echoed on the response, forwarded to the policy service, stored with the decision and the claim, and present in both services' logs.
- Reconciliation runs also carry their own run id.
- These are verified by `CorrelationIdSpansBothServices`.

## Security boundaries

| Boundary | Control | Verified by |
| --- | --- | --- |
| ledger → policy decisions | bearer decision credential, compared in constant time | `ServiceAuthenticationTests`, `HttpAuthenticationBoundaryTests`, multi-service |
| policy management API | a separate admin credential; the API is disabled (`403`) without one | same, plus `ManagementDisabledTests` |
| unrecognized policy paths | **denied by default** (`404`) on the container-normalized path | `HttpAuthenticationBoundaryTests`, which reproduces the path-parameter bypass fixed in Milestone 5 |
| request size | ledger: 64 KiB (app and Kestrel); policy: 256 KiB (declared and chunked); JSON depth 16 (ledger); lists ≤ 100 contexts, ≤ 100 rules, ≤ 50 items per rule list | `OperationsTests`, `FailureSimulationTests`, `HttpHardeningTests` |
| server banner | no `Server` header on either service | `FailureSimulationTests`, `HttpHardeningTests` |
| error bodies | problem details with a code; no exception text, SQL, stack traces or echoed input | `OperationsTests`, `HttpHardeningTests` |
| ledger runtime role | no DDL, no trigger disabling, no truncate, no writes to immutable history, claims or the migrations table, no role escalation | `LedgerRuntimeRoleTests` (32 probes), `verify-isolation.sh` |
| policy runtime role | the same, for the policy schema and the Flyway history | `RuntimeRoleTests`, `verify-isolation.sh` |
| cross-database access | each role can connect only to its own database | `verify-isolation.sh`, multi-service |

**Actor trust.** `X-Actor-Id`, on both services, is **client-asserted and unverified**:
- It is recorded in the audit and in the idempotency fingerprint, but it proves nothing about who called.
- The trust boundary is the network the services run on.
- Production needs an authenticated principal from the caller: workload identity or mTLS between services, and end-user authentication at the ledger edge. That principal would replace the header. This is out of scope here and is listed as a Milestone 6+ requirement.

**Idempotency keys:**
- Keys are 8–128 characters from `A–Z a–z 0–9 . _ : ~ -`, scoped by ledger and operation.
- A key reused with a different request is `409`. A key can't be used to read another caller's result: the fingerprint includes the actor, ledger and journal.
- Claims are kept forever, at roughly 300–400 bytes per posting or reversal including indexes. That's about 1 GB per 3 million postings.
- There is no expiry, because no requirement justifies one, and an expired key would reopen the duplicate-posting window.
- Denial of service is bounded: a client can only create claims by performing real postings or reversals, which already require approved journals.
- Raw keys are stored in the claim and the audit, where they are the investigation handle, but never logged.

## Troubleshooting

| Symptom | Look at | Likely cause / action |
| --- | --- | --- |
| ledger `/health/ready` 503 | the ledger log (`Ledger database unavailable`); `docker compose ps` | database down or unreachable; or migrations not applied (run the `ledger-migrate` job) |
| journals stay `PENDING_APPROVAL` | `approvalFailure` on the response; `/health/dependencies`; ledger `Policy evaluation failed … FailureKind` | policy down or slow (`UNAVAILABLE`/`TIMEOUT`): retry `request-approval` later. `AUTHENTICATION_FAILED`: the decision tokens differ between the services. `CONTRACT_VIOLATION`: a version mismatch |
| `409 IDEMPOTENCY_CONFLICT` | the claim for the key (`command_idempotency`); the key reference in logs | the client reused a key for a different request: use a new key |
| `503 LEDGER_DATABASE_UNAVAILABLE` under load | the pool size (`Maximum Pool Size` in the connection string) and database connections | an exhausted pool: raise the pool size or find long transactions |
| reconciliation `DISCREPANCY` | the category, the ids, and `ReconciliationRunId` in the logs; the `journal_status_transitions` and `command_idempotency` rows | the guards were bypassed; investigate, and correct only with a new correcting journal, never by editing history |
| `/ops/outbox` `AGING` | the pending count | expected until a relay exists |
| random database timeouts across tests | the wall clock (see Milestone 3/4 records) | host/WSL clock skew; resync the Windows clock |

Correlate any request across both services by its `X-Correlation-Id`: it appears in the ledger's JSON scope, in the policy's ECS `correlationId`, on the decision row and on the claim.

## Known limitations

- Actor identity is client-asserted, and service credentials are shared bearer tokens without TLS in local and Compose setups (ADR-013).
- The reconciliation and outbox diagnostics endpoints are unauthenticated, like the whole ledger API. They expose ids and aggregates, never payloads or credentials.
- There is no metrics backend; timings exist only in structured logs.
- There is no outbox publisher; `AGING` grows until one exists.
- The Maven dependencies were checked for available updates, not scanned for CVEs; the stack has no scanner. The .NET packages were checked against NuGet's advisory data.
