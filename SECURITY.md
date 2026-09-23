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

## Current limitations (as of Milestone 3)

- Ledger → policy calls use shared bearer credentials (ADR-013), with a separate credential for the policy management API, which is disabled when that credential isn't set. This proves possession of a secret, not workload identity. Without TLS the credential crosses the network in the clear, which is acceptable only on a trusted local network.
- There is no end-user authentication on the ledger API. `X-Actor-Id` headers on both services are recorded but client-asserted. A full security review is Milestone 5.
- Both services run as least-privilege runtime roles. Schema owners and superusers can still disable the database guard triggers (ADR-007).
- The policy service's OpenAPI and Swagger UI are enabled by default. Disable them (`springdoc.api-docs.enabled=false`) outside development.
