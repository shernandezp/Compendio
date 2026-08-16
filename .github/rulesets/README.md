# Branch protection ruleset

[`protected-branches.json`](protected-branches.json) is the repository ruleset that protects
`master` and `develop`. It is stored here as code so the rules are reviewable and reproducible.

## What it enforces

On both `master` and `develop`:

- **Pull request required** — no direct pushes, with **1 approving review** and all review
  conversations resolved.
- **Status checks must pass** — `client`, `server (ubuntu-latest)`, `server (windows-latest)`,
  `contract`, and `licence`.
- **No force-pushes** (`non_fast_forward`) and **no branch deletion**.

The **repository owner** (admin role) is on the bypass list and can merge without satisfying these.

## Applying it

Repository rulesets require the repository to be **public** (or on a paid plan). Once the repo is
public, apply — or update — the ruleset with:

```bash
# Create it the first time
gh api -X POST repos/shernandezp/Compendio/rulesets \
  -H "Accept: application/vnd.github+json" \
  --input .github/rulesets/protected-branches.json

# Update it later (RULESET_ID from: gh api repos/shernandezp/Compendio/rulesets)
gh api -X PUT repos/shernandezp/Compendio/rulesets/RULESET_ID \
  -H "Accept: application/vnd.github+json" \
  --input .github/rulesets/protected-branches.json
```
