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

## Current limitations (Milestone 0)

- There is no authentication on either service, or between them. Service-to-service authentication is Milestone 3 scope; a full security review is Milestone 5.
- The policy service's OpenAPI and Swagger UI are enabled by default. Disable them (`springdoc.api-docs.enabled=false`) outside development.
