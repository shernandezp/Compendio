# specs/

Design notes that more than one part of the implementation depends on. They carry decisions the
product overview does not, and the implementation specs reference them rather than restating them.

```
specs/
├── design/
│   ├── permissions.md      who can read what, who can edit what
│   ├── secure-content.md   encrypted pages, admin-only editing
│   ├── localization.md     Spanish + English, UI and content
│   └── search.md           FTS5 search that obeys permissions
└── adr/                    architecture decision records (see adr/README.md)
```

## Where everything lives

| Document | What it answers |
|---|---|
| [`../project-overview.md`](../project-overview.md) | What Compendio is, who it is for, why it exists, and what it refuses to be |
| [`../compendio-implementation/v0-implementation.md`](../compendio-implementation/v0-implementation.md) | How the MVP gets built: architecture, data model, decisions, acceptance criteria, build order |
| [`../compendio-implementation/v1-implementation.md`](../compendio-implementation/v1-implementation.md) | The same, for lifecycle / AI / directory integration / importers |
| `design/` (here) | The four capabilities whose reasoning is too long to sit inside an implementation spec |

The shape: a product document at the root, one implementation spec per version in
`compendio-implementation/`, shared design notes here, and the code as the final authority once it
exists.

## Status

Draft — 2026-07-30. Spec-only; no code yet. Sections marked *Open* are genuinely open and should be
resolved, and recorded in the relevant implementation spec's decision table, before the milestone
that depends on them.
