# ADR-013 — Service-to-Service Authentication

- Status: Accepted
- Date: 2026-09-23 (Milestone 3)
- Reviews required: @MurayaSoftTouch (policy service), @LMichy1 (ledger client)

## Context

Until Milestone 3 the policy service accepted any request. Anyone who could reach it could:
- ask for decisions, which also consumes their transaction ids;
- create and activate policy versions, which changes financial controls.

A full identity platform is out of scope, but "no authentication" can't be carried into a system that approves money movements.

## Decision

- **Two separate shared credentials**, supplied only through the environment (`POLICY_DECISION_API_TOKEN`, `POLICY_ADMIN_API_TOKEN`; on the ledger side, `Ledger__PolicyServiceToken`). Each must be at least 32 characters, and they must differ from each other. They are never committed, logged or included in `toString()`.
  - **Decision credential:** held by the ledger. It is **required**: the policy service refuses to start without it, and `POST /v1/policy-decisions` returns `401 SERVICE_AUTHENTICATION_REQUIRED` without it.
  - **Admin credential:** for the management API `/api/v1/**`. **If it is not configured, the management API is disabled** (`403 MANAGEMENT_API_DISABLED`). The decision credential is never accepted for management, and vice versa.
- Credentials travel as `Authorization: Bearer <credential>`. The policy service compares SHA-256 digests with `MessageDigest.isEqual`, which runs in constant time.
- Health, info and API docs stay unauthenticated.
- The ledger treats `401`/`403` as a configuration failure: fail closed, no retry.
- The contract documents this as **1.1.0** (a bearer security scheme and a `401` response).

## Alternatives

- **mTLS or workload identity** (SPIFFE, cloud IAM, service mesh). The right production answer, but it depends on the platform; there is no deployment target yet.
- **Signed JWTs from an issuer.** Would need an issuer, key rotation and clock handling, which is too much for this milestone.
- **One credential for everything.** Rejected: evaluating a transaction and rewriting the rules are different privileges.
- **Leave the management API unauthenticated but "internal".** Rejected: it changes financial controls.

## Consequences

- Both services need secret configuration to start. Compose reads the credentials from `.env`.
- Rotating a credential means restarting both services with the new value; there is no dual-credential overlap window yet.

## Known limitations

- **This is not zero trust.** A shared bearer credential proves possession of a secret, not the caller's identity. It can be replayed if intercepted. Without TLS it is sent in the clear on the network, which is acceptable only on a trusted local network. Production should use mTLS or platform workload identity, and put TLS on every hop.
- There are no per-user administrative identities or roles: `X-Actor-Id` is recorded but still client-asserted. Administrative authorization, ideally with four-eyes activation, is Milestone 5 scope.
- There is no rate limiting or lockout on authentication failures.
