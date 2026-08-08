# Design Note — Secure (encrypted) content

> Status: Draft v0.1 — 2026-07-30
> New capability, not in `project-overview.md` v0.2. Fold into §5.1 and §6 when the overview is next revised.
> Related: [`permissions.md`](permissions.md), [`search.md`](search.md).

---

## 1. The requirement

Some pages are genuinely sensitive: router credentials, licence keys, incident post-mortems, disciplinary procedures, the recovery runbook that lists where the keys are. Today, in a files-first wiki, those pages sit as plain Markdown on a Windows share — readable by anyone with a backup tape, a stolen laptop, or the file-server admin account that has nothing to do with the wiki's own accounts.

So: **a way to encrypt sensitive pages at rest, that only administrators can edit, that adds no external service, no key server, and no native dependency.**

"Safe and light" is the whole design constraint. Everything below chooses the boring, in-the-BCL option over the cryptographically fancier one.

## 2. Threat model — say it plainly

**Protects against:** someone who obtains the content folder without the service's keys. Stolen or discarded disk, a backup archive that lands in the wrong share, the git mirror pushed to a remote, a file-server admin browsing `\\server\wiki`, an over-broad file-sync client, a laptop with the content folder synced onto it.

**Does not protect against:** anyone who is root/Administrator on the running server (they can read the key and the process memory), a compromised Compendio admin account, or the service itself being malicious. Nor does it hide *structure*: folder and file names, sizes, and modification times stay in the clear (that is the price of keeping the navigation tree working from the file system).

This must be stated in the admin UI, in one sentence, on the screen where a folder is marked secure. Overstating what encryption buys is worse than not having it.

---

## 3. Model: secure scopes

Encryption is a property of a **folder**, called a *secure scope*, inherited by everything beneath it. Not a per-page flag: a page's attachments, images, and history all have to travel with it, and per-page flags produce folders that are half-encrypted in ways nobody can reason about.

Marking a folder secure is an admin-only action and does three things atomically:

1. Sets `inherit_parent = false` on its ACL (a secure scope is always explicitly listed — see `permissions.md` §3).
2. Creates a data key for the scope.
3. Queues re-encryption of everything already inside it.

**Hard rule layered on top of the permission model:** inside a secure scope, `write` and `manage` require global role `admin`, regardless of ACL entries. Non-admins can be granted `read` and nothing more. This is the "only administrators can edit" requirement, and it is enforced in the evaluator, not in the UI:

```
if (scope.IsSecure && user.Role != Admin)
    level = min(level, PermissionLevel.Read);
```

Nesting: a secure scope inside a secure scope is rejected with a clear message (one key per scope, no key hierarchies to reason about). Moving a page *into* a secure scope encrypts it; moving it *out* decrypts it, is admin-only, and is written to the audit log as a declassification event.

---

## 4. On-disk format

A secure page `runbook.md` is stored as `runbook.md.enc`. Attachments become `photo.png.enc`. The file is a self-describing envelope so that a future version — or a panicked admin with the recovery tool — can read it without guessing:

```
offset  size  field
0       8     magic         "CMPDENC1"
8       1     version       0x01
9       1     alg           0x01 = AES-256-GCM
10      16    key_id        GUID of the data key that wrapped this file
26      12    nonce         random per encryption, never reused with a key
38      N     ciphertext
38+N    16    auth tag
```

- **AES-256-GCM**, from `System.Security.Cryptography.AesGcm` — in the BCL, hardware-accelerated on every CPU we target, no native package, nothing to break single-file publishing or a chiselled container (§7.4, §7.7). XChaCha20-Poly1305 would be a defensible alternative but needs a third-party library, and library-free is the point.
- **AAD** = `key_id ‖ version ‖ logical path of the file`. Binding the path in means an attacker with write access to the folder cannot swap `Public/notes.md.enc` for `Secure/passwords.md.enc` and have the server decrypt it into the wrong place.
- Nonce is 12 random bytes per write. With a fresh nonce per save and a per-scope key, collision risk is negligible at wiki write volumes; a rekey counter is still tracked per key so `compendio doctor` can flag a key that has encrypted an implausible number of files.
- The plaintext is the **entire** Markdown file, front matter included. Titles and tags are sensitive too.

## 5. Keys

Two levels. No more.

```
Master key (MK)  — 32 random bytes, one per instance
   └─ wraps →  Data key (DEK) — 32 random bytes, one per secure scope
                  └─ encrypts → files in that scope, and their history snapshots
```

**Master key at rest** lives in `<data>/keys/master.key` (the directory §7.5 already reserves):

| Platform | Protection |
|---|---|
| Windows | DPAPI, `LocalMachine` scope, plus file ACL limited to the service account (`NT SERVICE\Compendio`) |
| Linux | File mode `0600`, owned by the `compendio` user, on a `ReadWritePaths`-restricted data dir |
| Container | Same as Linux; the operator is told to mount `keys/` as its own volume, and that losing it loses the secure pages |
| Any (optional) | **Passphrase mode**: MK is wrapped with a key derived from `COMPENDIO_MASTER_PASSPHRASE` (PBKDF2-HMAC-SHA256, ≥600 000 iterations, 16-byte salt, stored alongside). The service cannot start secure scopes without it. For orgs where "the disk was stolen with the OS on it" is in the threat model. |

**Data keys** are stored wrapped (AES-256-GCM under MK) in SQLite: `secure_scopes(id, folder_path, key_id, wrapped_dek, nonce, created_at, rotated_at)`. Wrapped DEKs in the database are useless without `keys/`, which is the property that makes the database itself safe to hand to a DBA.

**Rotation.** `compendio rekey --scope <path>` generates a new DEK and rewrites every file in the scope; `compendio rekey --master` generates a new MK and rewraps the DEKs only (cheap — files are untouched). Old key ids stay in the table marked retired so historical snapshots remain readable until they are rewritten.

---

## 6. Consequences that must be designed, not discovered

This is where a naive "just encrypt the files" feature breaks a files-first wiki. Each of these is a required work item, not a caveat.

### 6.1 Files-first is suspended inside a secure scope

You cannot open `runbook.md.enc` in VS Code. That is the deal, and it must be said in the docs in exactly those words. Mitigations:

- `compendio secure export <path> --out <file>` / `compendio secure import <file> --path <path>` — admin-only CLI round-trip for the rare direct edit.
- The watcher treats a plaintext `.md` appearing inside a secure scope as an ingest: encrypt it, then delete the plaintext (best-effort overwrite first; document honestly that secure deletion is not guaranteed on SSDs, journalling and copy-on-write file systems, or snapshotted volumes).
- The web editor is unaffected — it decrypts on read and encrypts on save, so for normal users nothing changes at all.

### 6.2 Search

Indexing decrypted content into FTS5 would copy every secret into the database in plaintext and quietly destroy the whole feature. Therefore:

- **Default: secure scopes are not indexed.** Their pages are findable by browsing, and by exact path, for users with `read`.
- An admin can opt in **per scope** to full-text indexing, behind a dialog that states plainly: *"Page contents will be stored unencrypted in the search index inside `compendio.db`. Anyone with the database file will be able to read them."* When enabled, those rows go in a separate FTS table so `compendio reindex --drop-secure` can purge them in one statement.
- Either way, results from secure scopes are filtered by the permission evaluator like everything else (`search.md` §4).

### 6.3 Page history

History snapshots of secure pages are stored in SQLite encrypted with the scope DEK (same envelope, path AAD = the logical page path). Diffs are computed in memory after decryption and never cached to disk. When a scope is declassified, its snapshots are rewritten in plaintext; when a folder is secured, existing snapshots are encrypted as part of the same background job.

### 6.4 Backup and restore — the data-loss trap

The obvious design ("exclude `keys/` from the backup, it's a secret") produces an archive that restores into unreadable garbage, discovered months later. The obvious opposite ("include `keys/`") produces an archive where the key sits next to the ciphertext, which means the encryption bought nothing.

Resolution: if any secure scope exists, `compendio backup` **requires** `--secure-passphrase` (prompted, or `COMPENDIO_BACKUP_PASSPHRASE`). The archive contains the ciphertext files plus the master key **rewrapped under that passphrase** (PBKDF2-HMAC-SHA256, ≥600 000 iterations). `compendio restore` asks for it. Refusing the passphrase refuses the backup, with a message explaining why. `compendio doctor` warns if the last backup predates the newest secure scope.

### 6.5 Everything else that touches page content

Each of these needs an explicit decision, and the decision is the same one:

| Surface | Behaviour |
|---|---|
| AI features | Secure scopes are excluded from AI context and per-page AI actions by default; an admin can allow a specific scope, with the endpoint named in the warning (§6 "AI privacy"). Never silently. |
| Static-site export | Secure scopes excluded, always. No opt-in — a DMZ HTML snapshot of the password page is not a feature. |
| Optional git mirror (v1) | Pushes the `.enc` files as-is. This is the case that makes the whole feature worth building. |
| Logs | Never log secure page content, titles, or decrypted anything, at any level. A log-scrubbing test asserts this. |
| `compendio doctor` | Reports: keys readable, every scope's DEK unwrappable, every `.enc` file's header parseable and key id known, count of files failing auth — without printing any plaintext. |
| Attachments endpoint | Decrypts in memory and streams; `Cache-Control: no-store`, no `ETag` derived from plaintext. |

### 6.6 Failure behaviour

If the master key is missing or unwrappable at startup, the service **starts**, serves all non-secure content normally, and shows secure scopes as unavailable with an explicit admin banner. It does not refuse to boot (a key problem must not take the whole wiki down) and it does not fail open. A file whose auth tag does not verify is reported as tampered/corrupt and is never partially rendered.

---

## 7. Why not the alternatives

| Alternative | Why not |
|---|---|
| Full-disk / BitLocker only | Protects a powered-off disk, not the running share, not the backup archive, not the git mirror. Complementary, not a substitute — recommend it in the docs anyway. |
| SQLCipher / encrypt the whole database | Content is not in the database (§4.1). Encrypts the wrong artefact, and adds a native dependency. |
| Per-user public-key encryption | Correct-er, and unusable: key distribution, key loss, re-encrypting on every ACL change. Wrong scale of product. |
| Age / GPG as an external tool | Another binary to install on the server. Violates §7.1 outright. |
| Encrypt everything, always | Kills the files-first promise for the 95 % of content that is not sensitive. Encryption stays opt-in and rare. |

---

## 8. Open questions

1. Should a secure scope support a *separate* unlock (an admin passphrase entered per session to view, distinct from server startup)? It would defend against a compromised admin session, at a real usability cost. Proposal: not in v1, revisit if asked for.
2. Compression before encryption — helps size, leaks length information. Proposal: no compression; Markdown files are small.
3. Should the file extension be `.md.enc` (visible, greppable, honest) or an opaque name (hides which pages are sensitive)? Proposal: `.md.enc` — hiding structure is out of the threat model anyway, and admins need to see what is protected.
