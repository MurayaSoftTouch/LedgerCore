## Summary

<!-- What changed and why. Link the issue: "Closes #123". -->

## Type

- [ ] feature
- [ ] bug fix
- [ ] chore / refactor
- [ ] docs

## Boundaries touched

- [ ] `contracts/` (both service owners must approve; version bumped per ADR-005)
- [ ] database schema / migrations
- [ ] cross-service behaviour

## Financial invariants

<!-- Delete what doesn't apply, and explain each item that does. -->

- [ ] Posted journals still always balance
- [ ] Posted history is not mutated (corrections via reversal)
- [ ] Policy failure still fails closed
- [ ] Retries cannot create duplicate postings
- [ ] Failures cannot leave partial financial state

## Verification

<!-- Commands you ran and their results. CI output alone is fine if it covers the change. -->

## Checklist

- [ ] Tests added or updated
- [ ] ADR added or updated if a decision changed
- [ ] No secrets or real customer data
- [ ] Commits follow Conventional Commits and are authored by the person who wrote them
