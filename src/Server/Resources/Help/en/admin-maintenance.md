# Maintenance and backups

**Administration → Status** shows version, install mode, content folder, page and folder counts,
file watching, search index state, indexing queue depth, database and content sizes, and when the
last backup ran.

## Backups

**Create a backup** writes an archive of the content and the database to the server's backups
folder.

If this instance has encrypted folders you are asked for a **backup passphrase**. It protects the
encryption key inside the archive. **Keep it somewhere other than this server** — you will need it
to restore, and a passphrase stored beside the backup protects nothing.

*Never — run `compendio backup`* under **Last backup** means exactly what it says. Schedule it.

Because pages are plain files, a folder-level backup or sync covers your content. It does **not**
cover version history, users, groups, access rules or acknowledgment records — those live in the
database. Back up both, which is what the backup command does.

From the command line:

```
compendio backup
compendio restore <archive>
```

## Search index

The index is a **cache**, never a source of truth. It is rebuilt from the content folder, and
deleting it costs nothing but a rebuild.

- **Rebuild the search index** — full rebuild, online and in batches. Users see a quiet notice that
  results may be incomplete while it runs.
- **Re-read the content folder** — reconciles Compendio's picture with what is actually on disk.
  Use this after copying files in directly, or if a page seems out of step.

From the command line:

```
compendio reindex
compendio reindex --drop-secure
```

`--drop-secure` also purges the text of encrypted folders that were opted into indexing.

## Health checks

- `/health` answers as soon as the process is up. This is what a container health check or load
  balancer should use — it deliberately does not depend on the index, so a rebuild does not get a
  healthy instance restarted.
- `/ready` reports index state, queue depth and rebuild progress. Information, not a verdict.

## Diagnostics

```
compendio doctor
```

Checks the installation and reports what is wrong — including an encryption key that cannot be
unwrapped, which is what **Key: Unavailable** on an encrypted folder means.

## Git mirror

**Administration → Integrations** shows the git mirror: whether it is enabled, the branch, when it
last pushed, and the last error. It is off unless configured with `GitMirror:Enabled` and a remote
URL, and it needs `git` on the server's PATH — if git is missing, the mirror stops and nothing else
is affected. **Push now** forces a push.

Owners are notified when a mirror push fails, so a broken mirror does not fail silently.
