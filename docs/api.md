# The Compendio API

`/api/v1`, JSON, cookie authentication. Same origin as the SPA, CORS disabled.

The OpenAPI document is served at `/openapi/v1.json` outside Production and committed to
`docs/openapi/v1.json`; CI regenerates it and fails on a diff, so the contract cannot drift silently.

---

## Conventions

**Paths.** A page path is content-relative with forward slashes: `IT/VPN/site-to-site.md`. It goes
in the URL for reads and writes (`/api/v1/pages/IT/VPN/site-to-site.md`) and in the body or query
for everything else — a catch-all route segment has to be last, so `/pages/{path}/move` cannot
exist.

**Paging.** `?page=1&pageSize=50` (max 100), returning `{ items, totalCount, page, pageSize }`.
`totalCount` uses the same permission predicate as `items`, so "12 results" means twelve results
*you* can see.

**Enums** travel as strings and are matched case-insensitively on the way in.

**Timestamps** are ISO-8601 UTC. The server never formats a date for display; the client does.

**Errors** are `ProblemDetails` with a stable machine `code` and a localized `title` and `detail`:

```json
{
  "title": "Página no encontrada",
  "detail": "No hay ninguna página en «IT/vpn.md», o no tienes acceso a ella.",
  "status": 404,
  "code": "page.not_found",
  "instance": "/api/v1/pages/IT/vpn.md"
}
```

Key on `code`. It does not change with the caller's language, and the prose always does.

### A page you cannot read returns 404, not 403

Deliberately indistinguishable from a page that does not exist. A 403 confirms the page exists,
which is exactly the leak the tree and search go out of their way to avoid.

`403` appears only when you *can* read something but not write it — a distinction that is safe to
make, because you already knew it was there.

---

## Error catalogue

| Code | HTTP | Meaning |
|---|---|---|
| `page.conflict` | 409 | Content hash mismatch. The body carries `currentContent`, `expectedHash` and `actualHash`. |
| `page.not_found` | 404 | Absent **or** unreadable. |
| `page.forbidden` | 403 | Readable but not writable. |
| `path.invalid` | 400 | Failed the path rules; `detail` names which one. |
| `path.too_long` | 400 | Exceeds the path budget. |
| `path.exists` | 400 | Something is already there. |
| `secure.admin_required` | 403 | Write inside an encrypted folder by a non-administrator. |
| `secure.unavailable` | 503 | Key missing or unwrappable. Everything else keeps working. |
| `secure.tampered` | 422 | The envelope failed authentication. Nothing is rendered. |
| `secure.nested` | 400 | An encrypted folder inside an encrypted folder. |
| `attachment.too_large` / `attachment.type_not_allowed` / `attachment.limit_reached` | 400 | Upload limits. |
| `search.query_invalid` | 400 | Rare — the parser is forgiving by design. |
| `acl.last_admin` | 400 | Would leave no active administrator. |
| `acl.invalid_subject` | 400 | The person or group does not exist. |
| `setup.completed` | 409 | Setup attempted after an administrator exists. |
| `validation.failed` | 400 | `errors` lists the fields. |
| `auth.failed` | 401 | Wrong credentials, or a deactivated account. One code for both, on purpose. |
| `auth.password_reused` | 400 | The new password is the one already in use. |
| `request.rate_limited` | 429 | |
| `ack.not_required` | 400 | The page is not marked as requiring acknowledgment. |
| `ack.version_mismatch` | 409 | The acknowledged version is no longer the one in force. |
| `ai.disabled` | 404 | No AI provider is configured, so the action does not exist. |
| `ai.provider_error` | 502 | The provider errored or answered with something unusable. Never carries the key. |
| `ai.timeout` | 504 | The provider exceeded `Ai:TimeoutSeconds`. |
| `ai.not_allowed_here` | 403 | Outside the allowed spaces, or an encrypted folder that has not opted in. |
| `ai.quota_exceeded` | 429 | The caller's daily AI allowance is spent. `scope`, `limit` and `resetsAt` ride along. |
| `ai.quota_exceeded_instance` | 429 | The instance's daily AI allowance is spent. Same extensions. |
| `git.unavailable` | 503 | `git` is not on `PATH`, or the remote rejected the push. |

---

## Endpoints

### Auth

| | |
|---|---|
| `POST /auth/login` | `{ userName, password, persistent }` |
| `POST /auth/logout` | |
| `GET /auth/me` | Works unauthenticated: returns `{ authenticated: false, needsSetup }` so the SPA can decide between the login screen and the wizard. |
| `PUT /auth/profile` | `{ displayName?, email?, preferredLanguage? }` |
| `POST /auth/password` | `{ currentPassword, newPassword }` |

### Setup

Reachable only while no user exists; afterwards it returns `setup.completed`.

| | |
|---|---|
| `GET /setup/state` | |
| `POST /setup` | `{ language, adminUserName, adminPassword, adminDisplayName, instanceName?, defaultAccess }` |

### Tree

`GET /tree` — the whole navigation tree, filtered by the permission evaluator, each node carrying
your effective level. Nodes you cannot read are **absent**, not present-and-greyed.

### Pages

| | |
|---|---|
| `GET /pages/{*path}?raw=true` | `raw` skips rendering and returns Markdown only. What the editor asks for. |
| `POST /pages` | `{ folderPath, title, content?, templateId?, lang?, translationKey? }`. The file name is slugified from the title — accents dropped, letter case kept, so "Índice" is `Indice.md` — and the accented title lives in front matter. A name that differs from an existing one only by case gets a `-2` suffix, because the folder may be a Windows share tomorrow. `templateId` names an entry from `GET /templates` and is the starting body when `content` is empty. |
| `PUT /pages/{*path}` | `{ content, expectedHash, normalized?, note? }`. `expectedHash` is required. |
| `DELETE /pages/{*path}` | The file is removed; its history is tombstoned for the retention window. |
| `POST /pages/move` | `{ path, targetPath }` |
| `POST /pages/checkbox` | `{ path, offset, checked, expectedHash }` — a byte substitution, not a re-serialization. |
| `GET /pages/backlinks?path=` | |

#### Saving, and conflicts

Every write is conditional on the hash you last read. Two writers produce a `409` carrying both
versions rather than a silent overwrite:

```json
{
  "code": "page.conflict",
  "path": "IT/vpn.md",
  "expectedHash": "9f2c…",
  "actualHash": "b31a…",
  "currentContent": "…the whole file as it is now…"
}
```

Both versions travel with the error because the client turns this into a three-pane merge. This is
the one moment a user could lose an hour's work.

#### Who writes Markdown

Only the client. remark in the browser is the sole Markdown serializer in the product; the server
stores the bytes it is handed after validating front matter, size and path. It renders and it
extracts text, and it has no serializer to accidentally reach for.

The one exception is the checkbox endpoint, which replaces `[ ]` with `[x]` at a known offset,
validated against the expected old text *and* the content hash. That is a two-character edit, not a
rewrite, which is why it is allowed.

### Folders

`POST /folders` · `POST /folders/move` · `DELETE /folders/{*path}`

Deleting a folder needs `manage`, not `write`: `write` is for the contents.

### Attachments

| | |
|---|---|
| `GET /attachments/{*path}` | An **authorized endpoint**, never a static file. `Cache-Control: no-store` and no `ETag` — an ETag derived from plaintext would let a cache confirm the contents of an encrypted file to somebody who cannot read it. |
| `POST /attachments` | `multipart/form-data` with `pagePath` and `file`. |
| `DELETE /attachments/{*path}` | Removes the images that showed the file from its page, then deletes the file. The page is read and rewritten inside the request, so a caller holding an old copy is not a conflict; a concurrent save is, and then nothing is deleted. |

Uploads are checked against an extension allowlist **and** content-type sniffing, both. Images are
served inline; everything else gets `Content-Disposition: attachment`.

The delete is one request because the two halves must not come apart: a file deleted without its
references leaves the page rendering a broken image. It edits the page through the store rather
than through `PUT /pages`, so it is not treated as a human revising the text — in particular, a
machine-translated page keeps its unreviewed flag. Links to the file are left alone; a sentence
somebody wrote is not ours to delete.

### Search

| | |
|---|---|
| `GET /search?q=&page=&pageSize=&in=` | |
| `GET /search/suggest?q=` | Quick switcher. |
| `GET /links/suggest?q=` | `[[link]]` autocomplete. |
| `GET /tags` | Counts recomputed per user, never cached globally. |
| `GET /recent?limit=` | |

Query syntax: `vpn cisco` (all terms, last one prefix-matched) · `"site to site"` · `-obsolete` ·
`tag:seguridad` · `space:IT` / `in:IT/VPN` · `owner:ana` · `lang:es` · `updated:>2026-01-01`.
An unknown `foo:bar` is searched for literally rather than rejected.

Every one of these surfaces carries the same permission predicate, in the SQL. Nothing is filtered
after the fact: post-filtering breaks paging, leaks totals, and produces empty pages that themselves
prove hidden matches exist.

### History

| | |
|---|---|
| `GET /versions?path=` | |
| `GET /versions/{id}` | The full content of one version. |
| `GET /diff?path=&from=&to=` | Both a source diff and a rendered one. |
| `POST /versions/{id}/restore` | `{ path }` |

Restoring writes a **new** version rather than rewinding, so a mistaken restore is itself undoable.
An external edit is recorded as an external edit, timestamped from the file and attributed to
nobody — never to whoever happened to be signed in.

Deleting a page tombstones its versions for `History:DeletedRetentionDays` instead of dropping them.
Bringing the page back is an administrator's action — see *Deleted pages* under Administration.

### Access rules

| | |
|---|---|
| `GET /acl/{*path}` | Needs `manage` on the folder. |
| `PUT /acl/{*path}` | `{ inheritParent, entries: [{ subjectType, subjectId, level }] }` |
| `GET /acl/effective?path=&userId=` | What one person can do here, and *why*. |

`inheritParent: true` means the folder may only **add** access. `false` means inheritance is cut and
the folder is **exactly** its own entry list. There is no third state and there are no deny entries —
every restriction anyone actually wants is expressible as the second option, which is also what the
UI says out loud.

You cannot grant more than you have.

### Administration

Requires the `Admin` role.

`GET|POST /admin/users` · `PUT /admin/users/{id}` · `POST /admin/users/{id}/password` ·
`DELETE /admin/users/{id}` · `GET|POST /admin/groups` · `PUT|DELETE /admin/groups/{id}` ·
`GET|POST /admin/secure-scopes` · `PUT /admin/secure-scopes/{*path}` · `GET /admin/audit` ·
`GET /admin/status` · `POST /admin/reindex` · `POST /admin/reconcile`

There must always be one active administrator. Every path that could break that — demote,
deactivate, delete — returns `acl.last_admin` instead.

#### Deleted pages

| | |
|---|---|
| `GET /admin/deleted-pages` | Pages whose file is gone and whose history is still held: id, last path, title, when it was deleted, how many versions. |
| `POST /admin/deleted-pages/{pageId}/restore` | `{ targetPath? }`. Writes the last version back where the page was, or at `targetPath`. `path.exists` when something now lives there. |

A restore keeps the page's identity, so every earlier version becomes its history again and the
restore itself is one more entry in it, after the delete. Both the file and the row come back
through the ordinary pipeline, so search, permissions and acknowledgments see a page, not a special
case.

### Lifecycle

`PUT /pages/lifecycle` (owner, review interval, next review date, requires-acknowledgment) ·
`POST /pages/review-confirm` · `GET /lifecycle/stale` · `GET /lifecycle/stale.csv` ·
`GET /dashboard` · `GET /users`

The path travels in the body or the query for the same reason it does on pages: a catch-all route
segment has to be last, so `/pages/{path}/lifecycle` never matches.

`owner` is a **username**, not a display name — the dashboard and the notification fan-out both need
a user id. An owner matching no active account is reported as unassigned and the front matter is left
exactly as written; eating a value a human typed would break the promise that the file is the source
of truth.

**An ordinary save does not reset the review clock.** `POST /pages/review-confirm` is the only thing
that does. Fixing a typo is not a review, and conflating the two would make the stale flag mean
"recently touched" rather than "recently checked".

`GET /users` is not the administration list: it returns id, username and display name and nothing
else, because setting an owner needs `write` on the page rather than the `Admin` role.

### Notifications

`GET /notifications` · `GET /notifications/count` · `POST /notifications/{id}/read` ·
`POST /notifications/read-all`

There is no email in this product, so this is where a stale page, an external edit to something you
own, and an acknowledgment you owe all arrive.

At most one **unread** row exists per `(user, kind, target)`, enforced by a filtered unique index: a
page stale for three months is one notification, not ninety. Reading it lets the same condition speak
again.

Every row's target is re-checked against the permission evaluator when the inbox is read, and a row
whose page the recipient can no longer read is dropped from the response and deleted. The count uses
the same filter — a badge counting rows the list then dropped would be the same leak in one number.

### Acknowledgments

`POST /acknowledgments` · `GET /acknowledgments/page?path=` · `GET /acknowledgments/user/{userId}` ·
`GET /acknowledgments/mine` · `GET /acknowledgments/report.csv?path=`

An acknowledgment records **a specific version**, so the report can never claim somebody read a
document that no longer exists. Giving one needs `read`; the report needs `manage` on the folder,
because a list of who has and has not done something is different information from the page itself.

Acknowledgment is re-opened only by an explicit `materialRevision: true` on `PUT /pages/{path}`. A
diff heuristic was rejected: re-asking two hundred people to re-read a typo fix is how the feature
gets switched off, and the author already knows which kind of change they made.

CSV exports are UTF-8 **with a BOM**, because Excel reads a BOM-less file as the system code page and
turns `Política` into mojibake.

### AI

`GET /ai/status` · `POST /ai/improve` · `/ai/summarize` · `/ai/freshness` · `/ai/draft` ·
`/ai/translate` · `/ai/ask` · admin `GET|PUT|DELETE /admin/ai`, `POST /admin/ai/test`

**With no provider configured every action returns `404 ai.disabled`.** `GET /ai/status` always
answers and reports `enabled: false`, which is what the client renders from — a control that only
fails when pressed is worse than no control. The routes are mapped unconditionally on purpose:
mapping happens at startup and configuration happens at runtime, so the alternative is restarting the
service after somebody pastes a base URL into a form.

Retrieval for `/ai/ask` filters by the caller's readable folders **before** any passage is read from
disk, encrypted folders are excluded unless the scope has opted in, and every path the model cites is
checked again before the response is sent. The model has no tools and cannot fetch anything, so
prompt injection inside a page can produce a wrong answer and nothing else.

`/ai/translate` is the one action that writes a file. The sibling page carries
`machineTranslated: true` in its front matter and renders an unreviewed banner; a human save from the
editor clears it, and the server strips the key rather than trusting the client to have done so.

The API key is stored encrypted, never returned, and appears in no error detail and no log line.

**Daily budget.** Every action is charged one request against a per-user and an instance-wide cap,
both measured over a rolling 24 hours and both settable on `PUT /admin/ai` (`dailyPerUser`,
`dailyPerInstance`; `0` removes a cap). Over budget is `429 ai.quota_exceeded` or
`ai.quota_exceeded_instance`, carrying `scope`, `limit` and `resetsAt`.

Three properties worth relying on:

- The charge lands **immediately before the provider call and after every permission check**, so a
  request refused with 403 or 404 costs nothing and a stranger cannot drain somebody else's
  allowance by asking about pages they cannot read.
- A request that then **fails at the provider stays charged**. A timeout arrives after the model has
  generated tokens, so refunding failures would refund the most expensive requests and make a retry
  loop free.
- `/ai/ask` makes two model calls — query expansion and the answer — and is charged **once**. The
  budget is a promise about actions, not about round trips.

`POST /admin/ai/test` is **not** charged: it is a diagnostic, and one that refused to run because the
instance had been busy would be a diagnostic nobody could trust. It is admin-only and still subject
to the global per-minute limiter.

`GET /ai/status` reports the caller's own `budget` alongside `enabled` and `features`, so a client
can warn before a limit rather than only explaining afterwards. `GET /admin/ai` adds the instance
total and its five heaviest users of the last 24 hours — counts and display names, never prompts.

### Meta

`GET /languages` · `GET /about` (version and the AGPL §5d notice) · `GET /health` · `GET /ready`

`/health` answers as soon as the process is up; it is what a container health check and a load
balancer use. `/ready` reports the search index state, which is information rather than a verdict —
making `/health` depend on it would restart a healthy container during a rebuild.

---

## Security posture

- **Cookie**: `HttpOnly`, `SameSite=Strict`, `Secure` under `Security:RequireHttps`. With CORS
  disabled and a same-origin SPA, that *is* the CSRF posture — there is no token machinery.
- **Rate limits**: login per client address, writes and searches per user.
- **CSP**: `script-src 'self'` with no inline script at all. `style-src` carries a per-response
  nonce, because Mermaid injects styles and `'unsafe-inline'` was ruled out.
- **Rendered HTML is sanitized server-side.** Pages contain pasted content, and a wiki where an
  editor can inject script into a reader's session is a stored-XSS machine. The CSP is defence in
  depth on top of that, not instead of it.
- **`Cache-Control: no-store`** on everything permission-dependent, which is nearly everything.
