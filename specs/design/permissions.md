# Design Note — Permissions layer

> Status: Draft v0.1 — 2026-07-30
> Extends `project-overview.md` §5.1 ("Users & permissions") with a concrete, implementable model.
> Related: [`search.md`](search.md) (search must obey this), [`secure-content.md`](secure-content.md) (adds a hard rule on top).

---

## 1. Design goals

1. **Explainable in one sentence per folder.** An admin must be able to look at a folder and answer "who can read this, who can edit this" without tracing rules. The UI shows exactly two states per folder: *inherits from parent* or *restricted to these people and groups*.
2. **Deny-by-default at the edges, permissive by default in the middle.** A fresh instance is readable by all authenticated users (§4 "simple, safe defaults"); anything an admin restricts is closed to everyone not listed.
3. **One evaluator, no second implementation.** Every read path — API, search, backlinks, link autocomplete, tag counts, attachments, exports, AI context — calls the same function. The UI never decides permissions; it only renders what the API already returned.
4. **No permission data in the content folder.** ACLs live in SQLite. If ACLs lived in front matter, any user with disk or git access could grant themselves rights, and a `git revert` could silently reopen a restricted folder.

Explicit non-goals for v1: per-block permissions, time-limited grants, request-access workflows, deny rules (see §4), anonymous/public spaces (backlog).

---

## 2. Model

### Subjects

| Subject | Notes |
|---|---|
| **User** | Local account in v0; LDAP/AD and OIDC accounts map onto the same row in v1. |
| **Group** | Local group with a member list. AD/OIDC groups sync into these by name in v1. |
| **Everyone** | Built-in pseudo-group: every *authenticated* user. Not anonymous — there is no anonymous access in v1. |

### Global roles (a ceiling, not a grant)

| Role | Ceiling | Meaning |
|---|---|---|
| `reader` | `read` | Can never write anywhere, whatever the ACLs say. |
| `editor` | `write` | Normal contributor. |
| `admin` | `manage` | Full access everywhere; the only role that can edit secure content and change global config. |

A global role never *grants* access to a restricted folder (except `admin`); it only caps what ACLs can give. This makes "make this person read-only across the whole wiki" a one-click operation that no folder ACL can override.

### Levels

Ordered, comparable integers so `max()` is meaningful:

| Level | Value | Can |
|---|---|---|
| `none` | 0 | Nothing. The node is invisible: absent from the tree, search, backlinks, autocomplete, tag counts. |
| `read` | 10 | View pages and attachments, search, export, acknowledge, see history. |
| `write` | 20 | Create/edit/move/delete pages and attachments in the subtree, restore versions. |
| `manage` | 30 | Everything in `write`, plus edit the ACL of this subtree and delete the folder itself. |

### Where ACLs attach

To **folders only**, at any depth. A *space* is nothing special — it is the folder at depth 1. Pages inherit from their folder; attachments inherit from their page's folder. Page-level ACLs are deliberately not supported: they are the main source of "why can't I see this" confusion in other wikis, and a one-page exception is always expressible as a one-page folder.

```
acl_nodes(id, path, inherit_parent, updated_at, updated_by)
acl_entries(node_id, subject_type /* user|group|everyone */, subject_id, level)
```

Folders with no `acl_nodes` row simply inherit — the common case stores nothing.

---

## 3. Evaluation

```
effective(user, folderPath):
    if user.role == admin: return manage

    level = instanceDefault            # config: read (default) | none (locked-down install)
    for node in ancestors(folderPath) + [folderPath]:      # root → target, in order
        acl = aclNodes[node]
        if acl is null: continue
        base    = acl.inherit_parent ? level : none
        granted = max(level of entries in acl matching user, their groups, or everyone; else none)
        level   = max(base, granted)

    return min(level, ceiling(user.role))
```

Two properties worth stating because they drive the UI:

- With `inherit_parent = true`, a folder can only **add** access. This is the "share this one folder with Finance" case.
- With `inherit_parent = false`, inheritance is cut and the folder is **exactly** its own entry list. This is the "restricted" case, and it is the only way to take access away.

There are deliberately **no deny entries**. Denies are where permission systems become unexplainable (order of precedence, user-vs-group conflicts, deny-on-an-ancestor surprises). Every restriction anyone actually wants is expressible as "cut inheritance and list who gets in", which is also what the UI says out loud.

### Invisibility, not locked placeholders

A node at `none` is omitted from every listing rather than shown greyed out. Folder names leak information ("Legal/Acquisition-Northwind"), and a placeholder invites support tickets. There is no config option for the other behaviour — small surface (§4.5).

---

## 4. Enforcement points

One interface, injected everywhere:

```csharp
public interface IPermissionEvaluator
{
    PermissionLevel Effective(UserContext user, ContentPath path);
    IReadOnlySet<int> ReadableFolderIds(UserContext user);   // for search; see search.md
    long Version { get; }                                    // cache epoch
}
```

Checklist of call sites — a PR that adds a new read path and does not appear here is incomplete:

- Tree/navigation endpoint (filter nodes).
- Page read, page write, move, delete, history read, version restore.
- Attachment/asset serving — **assets are served through an authorized endpoint, never as static files**. Serving `content/` as a static file root would bypass the entire layer; there must be a startup assertion that no static file provider is mapped over the content folder.
- Search query, search suggestions, tag browsing and tag counts, backlinks panel, `[[link]]` autocomplete.
- Static-site export and PDF export (export only what the invoking user can read).
- AI context assembly (RAG retrieval, "Ask the wiki" citations, page-level AI actions).
- Read-acknowledgment reports and owner dashboards.

### Caching and invalidation

Effective levels are computed from an in-memory snapshot of the folder tree plus ACL rows (both are small — hundreds to low thousands of nodes at SMB scale). Memoize per `(userId, folderId)` for the duration of a request, and keep a process-wide cache keyed by `Version`. Bump `Version` on: any ACL change, group membership change, role change, folder create/move/delete/rename. No fine-grained invalidation — the recompute is cheap and correctness matters more.

---

## 5. Interaction with the file watcher

Content is files; ACLs are database rows keyed by path. They can drift, and drift must never fail open.

- **Rename/move detected** (watcher correlates delete+create by content hash within a short window): the `acl_nodes.path` is rewritten for the node and all descendants, in one transaction with the tree update.
- **Path disappears without a correlated create**: the ACL row is *tombstoned*, not deleted, and kept for a configurable retention (default 30 days). If a folder of the same path reappears, the tombstone is revived. Dropping ACLs immediately would mean a folder deleted and re-synced by a backup tool comes back inheriting — i.e. wide open.
- **New path with no ACL row**: inherits from the nearest existing ancestor. Never defaults to instance-default when it sits under a restricted parent.
- Reconciliation runs on startup as well as live, and `compendio doctor` reports orphan ACLs, tombstones about to expire, and any folder whose ACL references a deleted user or group.

---

## 6. Audit

Every ACL change, role change, and group membership change writes an append-only `audit_log` row: who, when, subject, path, before, after. Not configurable off. This is the smallest thing that turns "someone opened up the HR folder" from an argument into a lookup, and it costs one insert.

---

## 7. UI shape (informative)

Folder settings, one screen:

```
Access to  IT / Infrastructure
( • ) Inherit from  IT          → currently: Everyone can read · Editors can write
(   ) Restricted — only the people and groups below

  [ + Add person or group ]
  Infra team      (group)   [ Write  ▾ ]
  Ana Rodríguez   (user)    [ Manage ▾ ]

Effective access preview:  [ pick a user ▾ ]  →  Ana Rodríguez can Manage (via Infra team)
```

The effective-access preview ("what can *this* person do here, and why") is worth building in the MVP: it converts the single most common support question into self-service, and it exercises the evaluator from the UI.

---

## 8. Open questions

1. Should `manage` be able to grant `manage` further down (delegated administration), or is granting capped at the granter's own level? Proposal: capped — you cannot grant more than you have.
2. Group nesting (groups containing groups). Proposal: not in v1; AD nested groups get flattened at sync time.
3. Do we need a `comment` level between `read` and `write` when comments arrive from the backlog? Leaving a numeric gap (10 → 20) so one can be inserted without a migration of stored values.
