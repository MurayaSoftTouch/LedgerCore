# Initial Backlog

Prepared in Milestone 0. **None of these exist as GitHub issues yet.** Issue numbers are assigned by GitHub when a contributor with write access creates them. Labels are defined in `.github/labels.yml`.

To create the labels (run as a contributor with write access, using your own account):

```bash
python3 -c "import yaml,shlex;[print('gh label create',shlex.quote(l['name']),'--color',l['color'],'--description',shlex.quote(l['description']),'--force') for l in yaml.safe_load(open('.github/labels.yml'))]" | sh
```

Each item below uses the feature or task issue template. "Owner" is the planned primary owner, not an assignment made on anyone's behalf.

## Ledger domain (Milestones 1 and 4, @LMichy1)

> Milestone 1 status: L1–L3 and the reversal model are implemented on `feat/m1-ledger-domain`. See [milestone-1.md](milestone-1.md) (M1-01 to M1-09). L4 idempotency keys and L5 hardening remain Milestone 4 scope.

### L1. Define account model
- **Labels:** `type:feature` `area:ledger` `area:database` `priority:p0`
- **Problem:** No chart of accounts exists, so journals have nothing to post against.
- **Scope:** Account entity (id, organization, code, name, type ASSET/LIABILITY/EQUITY/REVENUE/EXPENSE, currency, status). Migrations in the `ledger` database. Out of scope: balances and hierarchy.
- **Acceptance criteria:** Account codes are unique per organization. Type and currency are immutable once any entry references the account. Closed accounts reject new entries.
- **Dependencies:** ADR-002, ADR-004.
- **Tests:** Domain unit tests; migration test against PostgreSQL (Testcontainers once I2 lands).

### L2. Define journal and journal-entry model
- **Labels:** `type:feature` `area:ledger` `area:database` `priority:p0`
- **Problem:** The lifecycle (ADR-006) and the balance invariant (ADR-004) need a persistent model.
- **Scope:** Journal (state, currency, `reverses_journal_id`, decision references) and entries (account, side, positive `numeric` amount). Decision log table.
- **Acceptance criteria:** Only ADR-006 transitions are possible. A unique index on `reverses_journal_id` exists. Amounts use `numeric`, never float.
- **Dependencies:** L1, ADR-006 confirmed.
- **Tests:** State-transition tests covering every allowed and forbidden edge.

### L3. Implement double-entry validation
- **Labels:** `type:feature` `area:ledger` `priority:p0`
- **Scope:** Validate on submit and inside the posting transaction: at least one debit and one credit, all amounts > 0, a single currency, debits == credits.
- **Acceptance criteria:** An unbalanced journal cannot reach `PENDING_APPROVAL` or `POSTED`. A database-level guard rejects an unbalanced post even when the domain model is bypassed.
- **Dependencies:** L2.
- **Tests:** Property-based tests on random entry sets; a DB guard test issuing raw SQL.

### L4. Implement posting transaction
- **Labels:** `type:feature` `area:ledger` `area:database` `priority:p0`
- **Scope:** `APPROVED → POSTED` in one serialisable (or row-locked) transaction with an idempotency key.
- **Acceptance criteria:** A retry with the same key returns the original result and creates no second posting. Concurrent posts of the same journal produce exactly one `POSTED`. Failure mid-transaction leaves no partial state.
- **Dependencies:** L3, I4 (outbox), Milestone 3 policy client.
- **Tests:** Concurrency test with parallel requests; fault-injection test.

### L5. Implement reversal workflow
- **Labels:** `type:feature` `area:ledger` `priority:p1`
- **Scope:** Create a reversal journal that mirrors a posted journal with sides swapped, and runs through the normal lifecycle (`transactionType: REVERSAL`).
- **Acceptance criteria:** At most one reversal per journal. Reversals of reversals are rejected. The original row is never updated.
- **Dependencies:** L4.
- **Tests:** Double-reversal race test; balance-after-reversal equals balance-before-original.

## Policy service (Milestone 2, @MurayaSoftTouch)

### P1. Define policy schema
- **Labels:** `type:feature` `area:policy` `area:database` `priority:p0`
- **Scope:** Policy definitions: amount thresholds per currency/transaction type, account-type restrictions, review triggers. Migrations in the `policy` database.
- **Acceptance criteria:** A definition can express every example rule in ADR-003. Invalid definitions are rejected at write time.
- **Dependencies:** ADR-003, contract v1.
- **Tests:** Repository tests against PostgreSQL.

### P2. Implement policy versioning
- **Labels:** `type:feature` `area:policy` `priority:p0`
- **Scope:** Immutable, numbered versions (`name@n`); exactly one active version per policy.
- **Acceptance criteria:** A version used by any decision can't be edited or deleted. Activation is atomic.
- **Dependencies:** P1.
- **Tests:** Concurrent activation test; immutability test.

### P3. Implement approval evaluation
- **Labels:** `type:feature` `area:policy` `priority:p0`
- **Scope:** Pure evaluation function: request + policy version → decision + reason codes.
- **Acceptance criteria:** Deterministic. Every `REJECTED`/`REVIEW_REQUIRED` carries at least one reason code. An unknown `transactionType` yields `REVIEW_REQUIRED`.
- **Dependencies:** P2.
- **Tests:** Table-driven tests per rule; boundary amounts using decimal strings.

### P4. Expose policy decision API
- **Labels:** `type:feature` `area:policy` `area:contracts` `priority:p0`
- **Scope:** `POST /v1/policy-decisions` exactly per `contracts/openapi/policy-decision.v1.yaml`. Decisions persisted with `decisionId`. Returns `503` rather than guessing when evaluation cannot be trusted.
- **Acceptance criteria:** Responses validate against the response schema. A repeated `transactionId` under the same policy version returns the original decision.
- **Dependencies:** P3, I1.
- **Tests:** Spring integration tests; schema validation of responses.

## Integration (Milestone 3, @Ngetich-86)

### I1. Define ledger-policy OpenAPI contract
- **Labels:** `type:feature` `area:contracts` `priority:p0`
- **Status:** v1.0.0 skeleton delivered in Milestone 0 (`contracts/`). Remaining: review by all three owners, and service-to-service authentication.
- **Acceptance criteria:** Approved by both service owners. `contracts/validate.sh` passes in CI.

### I2. Implement contract tests
- **Labels:** `type:feature` `area:contracts` `area:integration` `priority:p0`
- **Scope:** The ledger client and the policy server are both tested against the shared schemas and examples.
- **Acceptance criteria:** A breaking schema change fails CI on both sides.
- **Dependencies:** I1, P4, I3.

### I3. Implement policy client
- **Labels:** `type:feature` `area:ledger` `area:integration` `priority:p0`
- **Scope:** Typed .NET client with a timeout (`Ledger:PolicyDecisionTimeoutMs`), retries on retryable outcomes only, response schema validation and a `transactionId` match check.
- **Dependencies:** I1.

### I4. Define failure behavior
- **Labels:** `type:feature` `area:integration` `priority:p0`
- **Scope:** Implement the ADR-005 outcome table: explicit rejection, manual review, timeout, internal failure and contract incompatibility, each recorded distinctly.
- **Acceptance criteria:** No failure mode results in `APPROVED`. Each is covered by a simulated failure test.
- **Dependencies:** I3.

### I5. Implement outbox pattern
- **Labels:** `type:feature` `area:ledger` `area:database` `area:integration` `priority:p1`
- **Scope:** Posting writes an outbox row in the same transaction; a relay publishes events at least once.
- **Acceptance criteria:** A crash between commit and publish loses no event; consumers can deduplicate.
- **Dependencies:** L4.

## Infrastructure (Milestones 0, 3 and 5, @Ngetich-86)

### X1. PostgreSQL environment
- **Labels:** `type:chore` `area:database` `area:devops` `priority:p0`
- **Status:** Delivered in Milestone 0: `docker-compose.yml`, per-service databases and roles, isolation check. Remaining: containerised services (Milestone 3).

### X2. Testcontainers
- **Labels:** `type:chore` `area:integration` `area:devops` `priority:p1`
- **Scope:** PostgreSQL Testcontainers for both the .NET and Java test suites, running the real init script.

### X3. CI workflow
- **Labels:** `type:chore` `area:devops` `priority:p0`
- **Status:** Base workflow delivered in Milestone 0 (`.github/workflows/ci.yml`). Not yet run on GitHub. Remaining: confirm the first green run; enable branch protection requiring it.

### X4. Structured logging
- **Labels:** `type:feature` `area:devops` `area:integration` `priority:p1`
- **Scope:** Both services already log JSON (ledger: JSON console formatter; policy: ECS). Remaining: correlation-ID propagation across the contract (`X-Correlation-Id`) and a shared field vocabulary.
