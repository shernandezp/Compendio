# People and groups

**Administration → People** and **Groups**.

## Roles

Every account has one role, and the role is a **ceiling, not a grant**:

| Role | Ceiling |
|---|---|
| **Reader** | Can never write, whatever a folder's rules say |
| **Editor** | Can write where a folder allows it |
| **Administrator** | Everything, including these screens |

This matters when you are debugging "why can this person not edit?". Check the role first: a folder
rule granting Write to a Reader does nothing. The access screen tells you so directly — it shows
*capped by their Reader role*.

## Adding a person

**Add a person** takes a user name, a password, a display name, an optional email and a role.

There is no email delivery in Compendio, so there is no invitation link and no self-service
password reset. You set the initial password and tell the person yourself. Likewise, a locked-out
user needs you to **Set a new password**.

Leaving the password blank when editing an existing person keeps their current one.

## Deactivating

Prefer **Active → off** over deleting. It stops the person signing in while keeping their name
against the edits they made, so history stays readable.

Compendio will not let you deactivate or demote the last active administrator. If that is the
account you are locked out of, recovery is a command on the server.

## Groups

A group is a named set of people. Grant access to the group, not to the individuals — then adding
somebody to the department is one action rather than a sweep through every folder.

Group nesting is not supported: a group contains people, not other groups. This is deliberate;
nested groups make "why can this person see this?" much harder to answer.

**Manage members** edits the membership. The member count is shown beside each group.

## Suggested shape

Create groups that mirror how your organization actually decides access — usually departments, plus
one or two cross-cutting ones like *on-call*. Grant folder access to those groups. Reserve
per-person grants for genuine exceptions, and expect to have very few.

## Audit log

**Administration → Audit log** records who did what and when — role changes, access changes,
encrypted-folder changes. It is the first place to look when something is not as you left it.
