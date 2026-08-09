# Access and permissions

**Administration → Access**, or the access screen for any folder.

Permissions are set **per folder**, and pages inherit from the folder they are in. There are no
per-page rules — that is what keeps "who can see this?" answerable.

## The four levels

| Level | Can |
|---|---|
| **No access** | Nothing. The folder is invisible. |
| **Read** | Read pages |
| **Write** | Read and edit pages |
| **Manage** | Read, edit, and change this folder's access rules |

Remember the role ceiling: a Reader granted Write still cannot write.

## Inheritance

By default a folder **inherits from its parent**, and the top level inherits the instance default
chosen during setup — usually "everyone who signs in can read".

To restrict a folder, switch it to **Restricted — only the people and groups below** and list who
gets in. That cuts inheritance at that point.

## There are no deny rules

This is deliberate, and it is the single most important thing to understand here.

You cannot take access away from somebody who has it through inheritance. To restrict a folder, you
**cut inheritance and list who gets in**. Deny rules in other systems produce permission sets that
nobody can reason about — "read here, denied there, but a member of two groups, one of which…" —
and the failure mode is silent over-exposure.

## Check your work

**Effective access preview** answers *what can this person actually do here?* for a named person,
and tells you **why**:

- *because they are an administrator*
- *capped by their Reader role*
- *from the instance default*
- *inherited from a parent folder*
- *because this folder is encrypted, and only administrators can change it*

Use it after every non-obvious change. It is faster than reasoning it out, and it is the same
evaluator the rest of the product uses, so it cannot disagree with reality.

## What restriction actually hides

A restricted page is invisible everywhere, not just in the tree: search results and result counts,
the Ctrl-K switcher, `[[link]]` suggestions in the editor, backlinks, tag counts, recently-updated
lists, the AI assistant's sources, and exports. A user cannot learn that a page exists from any of
them.

This is why a missing page reports *"does not exist, or you do not have access"* rather than a
permission error: a 403 would confirm the page is there.

## Moving a folder

Access rules move with the folder. A page never briefly becomes public because it is in transit.
