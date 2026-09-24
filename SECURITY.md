# Security Policy

## Reporting

Report vulnerabilities privately through [GitHub Security Advisories](https://github.com/MurayaSoftTouch/LedgerCore/security/advisories/new). Don't open a public issue. Include reproduction steps, affected commit and impact.

Treat as security-relevant anything that could:

- post an unbalanced journal, or mutate or delete posted history;
- approve a transaction without a valid policy decision (a fail-open path);
- create duplicate postings or leave partial financial state;
- let one service read or write the other's database;
- expose balances, credentials or personal data in logs or contracts.

## Supported versions

Pre-release. Only `main` is supported.

## Secrets

- `.env.example` holds throwaway local-development values only. `.env` is git-ignored.
- Credentials must never be committed. If one is committed, rotate it first, then remove it.
- The PostgreSQL port is bound to `127.0.0.1` only.

## Security boundaries (Milestone 5)

See [operational hardening](docs/architecture/operational-hardening.md#security-boundaries) for the full table and the tests behind each row.

- **Service authentication (ADR-013).**
  - The decision API needs the ledger's decision credential. The management API needs a separate admin credential, and is disabled when none is configured.
  - The policy service **denies unrecognized paths by default**, and classifies requests on the container-normalized servlet path.
  - Milestone 5 fixed an authentication bypass here. Path-parameter variants (`/v1/policy-decisions;x=y`, `/api;x=y/v1/policies`) reached protected endpoints without a credential, because the filter matched the raw request URI. `HttpAuthenticationBoundaryTests` reproduces it over real HTTP. Any deployment of a build before `84bae74` is affected: rotate the admin credential and review the policies and policy versions it holds.
- **Database roles (ADR-007).**
  - Both services run as least-privilege runtime roles that can't migrate, run DDL, disable triggers, truncate history or write immutable rows. There are destructive probes for each (`LedgerRuntimeRoleTests`, `RuntimeRoleTests`).
  - The migration owners are separate credentials, used only by the migration job and Flyway.
- **Input limits.**
  - Ledger bodies are capped at 64 KiB and policy bodies at 256 KiB. JSON depth is 16 on the ledger.
  - Every array is bounded.
  - Oversized requests fail with `413`.
- **Errors and logs.**
  - Errors are problem details with a stable code, and never contain exception text, SQL or echoed input.
  - Logs never contain credentials, passwords, connection strings, request bodies or raw idempotency keys. This is proven by log-capture tests in both services and in the multi-service suite.
- **Dependency review (Milestone 5).**
  - .NET: `dotnet list package --vulnerable --include-transitive` reports no known-vulnerable packages (nuget.org advisory data).
  - Maven: dependencies were checked for newer releases only; there is no CVE scanner in the stack. Spring Boot 4.1.1 is the latest GA release, and its managed versions (Tomcat, Jackson, the PostgreSQL driver) are current for that line.
  - **Neither check proves there are no vulnerabilities.**

## Current limitations (as of Milestone 5)

- **Shared bearer credentials (ADR-013).** Ledger → policy calls use shared bearer credentials, with a separate credential for the policy management API that is disabled when unset. This proves possession of a secret, not workload identity. Without TLS the credential crosses the network in the clear, which is acceptable only on a trusted local network.
- **Client-asserted actors.** There is no end-user authentication on the ledger API. `X-Actor-Id` headers on both services are recorded in the audit and in idempotency fingerprints, but they are **client-asserted**: the trust boundary is the network. Production needs an authenticated principal (workload identity / mTLS between services, end-user authentication at the ledger edge) to replace them.
- **Unauthenticated ledger endpoints.** The ledger's reconciliation (`/api/v1/ledgers/{id}/reconciliation`) and outbox diagnostics (`/ops/outbox`) endpoints are unauthenticated, like the rest of the ledger API. They expose ids and aggregates, never payloads or credentials.
- **Guard bypass by owners.** Schema owners and superusers can still disable the database guard triggers (ADR-007). The reconciliation report detects the financial effect of such a bypass.
- **OpenAPI enabled by default.** The policy service's OpenAPI and Swagger UI are enabled by default. Disable them (`springdoc.api-docs.enabled=false`) outside development.
