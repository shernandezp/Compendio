# Contributing to Compendio

Thanks for helping improve Compendio. This page covers how the repository is organised and what a
change has to clear before it can merge.

## Branching model

| Branch | Purpose | Direct pushes |
|---|---|---|
| `master` | Released code. Tagged releases are cut from here. | Protected — PR only |
| `develop` | Integration branch. **Open your pull requests against this one.** | Protected — PR only |
| `feature/*`, `fix/*` | Your work, branched from `develop`. | Free |

Release flow: `feature/*` → `develop` → `master`.

## Rules for merging

Both `develop` and `master` are protected by a repository ruleset. To merge you need:

- a **pull request** — direct pushes are blocked;
- at least **one approving review**;
- **all CI checks green** (`client`, `server`, `contract`, `licence`);
- every **review conversation resolved**;
- no force-pushes and no branch deletion.

The **repository owner is on the bypass list** and can merge without satisfying these — that is for
hotfixes and administrative changes, and is meant to be used sparingly.

## Reporting bugs

Open an issue with the **Bug report** template. A good report includes what you expected, what
happened instead, the **version** (`GET /api/v1/about` or the footer of any page), how the instance
is running (Windows Service, systemd, Docker, or standalone), and any logs — **with secrets and
encrypted-folder contents removed**.

Found a security problem? Report it privately through GitHub's **Report a vulnerability** button on
the Security tab rather than opening a public issue.

## Making a change

1. Branch from `develop`.
2. Build and test:
   ```bash
   dotnet build
   dotnet test
   ```
   For client work, also run the checks in `src/client`: `npm run check:i18n`, `npx tsc -b`, and
   `npx vitest run`.
3. Keep every user-facing string in **both Spanish and English**.
4. Update documentation when behaviour or the API changes, and regenerate the API contract if you
   touched an endpoint (see [`docs/development.md`](../docs/development.md)).
5. Open a pull request against `develop` and fill in the template.

## Licence

Compendio is **AGPL-3.0-or-later**. By contributing you agree your changes are licensed under it,
and every dependency must be licence-compatible — CI rejects one that is not (see
[`.github/scripts/check-licences.py`](scripts/check-licences.py)).
