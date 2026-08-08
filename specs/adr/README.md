# Architecture decision records

**The working decision trail is not here.** It is the *Decisions resolved in this refinement* table
at the end of each implementation spec, plus the `— DECIDED` sections inside them. That is where a
decision gets made, and where the reasoning stays next to the thing it constrains.

This folder exists for the subset that outlives the release it was made in, and it fills up at
**v0.9**, when the repository opens: overview §8 promises contributors an `specs/adr/` folder so they
can see *why* before proposing to undo something, and a public contributor will not read a v0
implementation spec to find out why there are no deny rules in the permission model.

Write one when the repo goes public, extracted from the decision tables, for at least:

| Decision | Recorded in |
|---|---|
| .NET 10 LTS; single-project Clean Architecture | v0 §Architecture |
| SQLite only, no PostgreSQL, no tenancy — and raw SQL confined to the search module | v0 §Platform decisions |
| Folder ACLs with no deny entries; roles as a ceiling | [`../design/permissions.md`](../design/permissions.md) |
| Folder-scoped AES-256-GCM secure content; what it does and does not protect | [`../design/secure-content.md`](../design/secure-content.md) |
| `remove_diacritics 2` and no stemming; permission predicate in the SQL | [`../design/search.md`](../design/search.md) |
| Sibling-file translations; a language is data | [`../design/localization.md`](../design/localization.md) |
| SQLite snapshots for page history, not an embedded git repo | overview §7.7, v0 §Page history |
| No SMTP, ever; in-app notifications only | overview §5.3 |
| Three deployment modes only; self-contained single-file, untrimmed, no NativeAOT | overview §7, v0 §Packaging |
| AGPL-3.0-or-later | v0 §Decisions |
| WYSIWYG component choice | v0 milestone 0 spike — **not yet made** |

Format: [`0000-template.md`](0000-template.md), one decision per file, `NNNN-short-title.md`. Never
edit an `Accepted` record — supersede it with a new one and mark the old `Superseded by NNNN`.
