# Encrypted folders

**Administration → Encrypted folders.** Encrypts the files of a chosen folder on disk.

## What this protects — and what it does not

**It protects:** a stolen disk, a backup archive, a mis-synced folder, and a future git mirror.

**It does not protect against:** an administrator of this server. And it **does not hide folder or
file names** — only contents.

If your concern is "somebody at this company should not read this", that is a job for access rules,
not encryption. Encryption is for "this data left the building on a disk".

## The cost: files-first is suspended

Compendio's usual promise is that every page is a Markdown file you can open in any editor. Inside
an encrypted folder that stops being true — `runbook.md.enc` does not open in VS Code.

To edit such a file directly you use `compendio secure export` and `compendio secure import` on the
server. Weigh this before encrypting a folder people actually work in daily.

Only administrators can change pages inside an encrypted folder. Anyone with read access can read
them normally through the web interface.

## Two switches worth understanding

### Include these pages in search

**Off by default, and think before turning it on.** Turning it on stores the page text
*unencrypted* in the search index inside `compendio.db`. Anyone with that database file can read
it — which substantially weakens the thing you turned encryption on for.

Left off, the pages are simply absent from search for everybody.

### Allow AI features to read these pages

Also off by default. Turning it on means the contents of this folder are sent to the configured AI
endpoint whenever somebody uses an AI action on a page inside it. The confirmation names the
endpoint. Left off, the assistant cannot read these pages and will never cite them.

## Keys and backups

The folder's status shows whether the key is **Readable** or **Unavailable**. Unavailable means the
key cannot be unwrapped on this machine — run `compendio doctor` on the server. Non-encrypted
content keeps serving normally in the meantime.

**Backups of an instance with encrypted folders require a passphrase.** It protects the encryption
key inside the archive. Keep it somewhere other than this server, because you will need it to
restore, and a passphrase stored beside the thing it protects is not a passphrase.

Nesting an encrypted folder inside another is not allowed.
