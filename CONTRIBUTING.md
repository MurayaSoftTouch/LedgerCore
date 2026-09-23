# Contributing

## Workflow

1. Pick or open an issue using a template (feature, bug, technical task).
2. Branch from `main`: `<type>/<short-description>`, e.g. `feat/journal-model`.
3. Keep PRs focused. Fill in the PR template, including the financial-invariants section.
4. Get a review from a contributor **other than the author**. `CODEOWNERS` requests the right person. Contract changes need both service owners.
5. Squash or rebase-merge once CI is green.

## Commits

[Conventional Commits](https://www.conventionalcommits.org/). Scopes: `ledger`, `policy`, `contracts`, `infra`, `ci`, `repo`, `docs`, `architecture`.

```text
feat(ledger): reject unbalanced journals on submit
fix(policy): return 503 when the policy store is unavailable
```

## Identity and attribution

Several contributors' credentials may live on one machine. Before committing, check that the repository-local identity is **yours**:

```bash
git config user.name && git config user.email
git config user.name "<you>" && git config user.email "<your email>"   # repo-local, not --global
```

- Commits are authored by the person who wrote the change. Don't author commits, open PRs or submit reviews under another contributor's identity or `gh` account.
- Don't rewrite published history or alter timestamps.
- Don't add automated co-author trailers. Add `Co-authored-by` only for a real human collaborator.
- If `gh` holds several accounts, check `gh auth status` before any action on GitHub.

## Local checks before pushing

```bash
(cd services/ledger-api && dotnet format --verify-no-changes && dotnet test)
(cd services/policy-service && ./mvnw -B spotless:check verify)
./contracts/validate.sh
```

## Rules that are never negotiable

- No `UPDATE` or `DELETE` of posted journals or their entries.
- No floating-point money, in code, SQL or JSON.
- No approval path that bypasses a valid policy decision.
- No secrets in the repository. Only `.env.example` holds local-development defaults.
