# Writing pages

**You never have to type Markdown.** The editor is a formatted editor — what you see is what the
page will look like. The file underneath is Markdown, which is what makes it readable forever, but
that is the file's business rather than yours.

## Creating a page

Use **New page** in the tree, in the folder where it belongs. You are asked what the page is about;
that becomes its title. You can also pick a **template**:

| Template | For |
|---|---|
| Blank page | Anything |
| Procedure | Step-by-step instructions |
| Runbook | What to do when something breaks |
| Policy | Rules people have to follow |
| Meeting notes | Decisions and actions |

Templates are a starting shape, not a constraint. Delete anything you do not need.

## Writing

Press **/** anywhere in the editor for a menu of things to insert — headings, lists, checklists,
quotes, code, code blocks, links, images, tables, dividers and diagrams. The toolbar has the same
options.

The usual shortcuts work: **Ctrl-B** for bold, **Ctrl-I** for italic.

Pasting from Word, a browser or an email keeps the formatting and converts it. If you want the text
without any of that, use **Paste without formatting**.

### Linking to another page

Type `[[` and start typing a page name. Only pages you can read are suggested, and the link stays
valid if the target page is later moved or renamed.

### Attachments

**Add a file** attaches a document, image or archive to the page. Attachment file names are
searchable.

## Saving

Press **Save**. If you navigate away with unsaved changes you are warned first.

If your browser or computer dies mid-edit, your text is kept locally and offered back to you the
next time you open that page — *Recovered an unsaved draft*. Take it or discard it.

### If somebody else saved while you were writing

You get a **conflict** screen showing your version and theirs side by side. Nothing is lost and
nothing is overwritten silently; you choose what to keep. Resolving a conflict needs a reasonably
wide screen, so on a phone you will be asked to finish it on a computer.

### If you are told you cannot save

*"You do not have permission to save here"* means you can read this folder but not write to it.
Your text is still in the editor — copy it somewhere before navigating away, and ask an
administrator for write access.

## Editing the file directly

Because every page is a real Markdown file, you can also edit it in VS Code or any editor, on the
server's content folder. Compendio notices, records a version, and shows the page as *updated in
the content folder*. The first time you save such a page here, its formatting is tidied once.
