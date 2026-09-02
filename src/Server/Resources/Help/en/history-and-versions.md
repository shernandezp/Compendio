# History and versions

Every change to every page is recorded. Nothing you do in Compendio destroys the previous text.

## Looking at the history

**History** on any page lists its versions, newest first. Each entry shows when it happened, who
did it, and why:

| Label | What happened |
|---|---|
| **Edited here** | Somebody saved from the editor |
| **Edited in the content folder** | The file was changed directly on the server |
| **Moved** | The page was moved or renamed |
| **Deleted** | The page was deleted — its history survives |
| **Restored** | An older version was brought back |
| **Formatting tidied** | A page written outside Compendio had its formatting normalized, once |

A change made in the content folder has **no author** — the file system does not record who wrote
it. This is expected on an instance where people also edit files directly.

## Comparing

Select two versions and press **Compare**. You get the differences two ways:

- **Source** — line by line, showing exactly what changed in the text.
- **Rendered** — the page as it looked, with additions and removals marked.

Source is better for a small wording change; rendered is better for seeing a restructured page.

## Restoring

**Restore this version** brings an old version back. It does not delete anything: restoring *adds a
new version* whose content is the old text. So a restore can itself be undone, and the record of
what happened stays complete.

## Deleted pages

Deleting a page removes the file but keeps the history. An administrator can bring it back from
**Administration → Deleted pages**: the page returns where it was, with its last text and its whole
history, and the restore itself appears in that history. If something else has been created at the
old path in the meantime, the administrator chooses another one.

History of a deleted page is kept for a retention period set by the administrator; after that it is
removed for good.

## Where this is really kept

The current text of every page is a Markdown file in the content folder — you can open it, copy it,
back it up, or put the folder in git. The version history lives in Compendio's database alongside
it. Backups cover both; see the administration guide.
