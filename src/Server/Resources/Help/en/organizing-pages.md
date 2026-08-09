# Organizing pages

## Folders

Folders are real folders on the server, and they are also the unit of access control — who can read
or write is decided per folder. That makes the folder structure worth a little thought.

A structure that works for most organizations is one top-level folder per area — *IT*, *HR*,
*Procedures* — with pages inside. Deep nesting tends to hide things.

Use **New folder** in the tree. **Delete this folder** removes it and everything inside; you are
told how many pages that is first, and the history of every one of them is kept and can be
restored.

## Moving and renaming

**Move or rename** on a page does both: change its name, its folder, or both at once.

- Names cannot contain `/` or `\` — choose the destination folder instead.
- Windows-illegal characters (`< > : " | ? *`, a trailing dot or space) are refused, because the
  content folder has to work on every operating system.
- Folders you can only read are marked *read-only* and cannot be chosen as a destination.

Links to a moved page keep working.

## Titles

**Change title** changes what everyone sees. The file name stays as it is, so titles can keep their
accents and punctuation while file names stay simple and portable.

## Tags

Tags cut across folders: a page lives in exactly one folder but can carry several tags. They are
good for things that are true of pages in many places — `security`, `onboarding`, `sql-server`.

The **Tags** screen lists them all with counts. Keep the vocabulary small; twenty tags used
consistently beat two hundred used once each.

## Links between pages

Type `[[` in the editor to link to another page. Every page shows **Linked from**, listing the
pages that link to it, which is often how you discover that a procedure is referenced from three
places you did not know about.

Links to pages a reader cannot see simply do not resolve for them, so the link list never becomes a
way to discover restricted page names.

## Ownership

Every page can have an **owner** — the person responsible for keeping it correct. Owners get the
review reminders. A page with no owner still works; it just means nobody is being asked about it.
See *Reviews and acknowledgments*.

## Two languages

Translations live beside the original rather than in a parallel tree: `remote-work.md` and
`remote-work.es.md`. Both appear as one document with a language switcher. See *Reading a page*.
