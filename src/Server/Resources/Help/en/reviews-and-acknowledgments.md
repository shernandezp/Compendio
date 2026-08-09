# Reviews and acknowledgments

These are the two features that keep a wiki honest. One asks *"is this still true?"*; the other
asks *"has everyone read this?"*.

## Ownership and review cycles

Open **Review and ownership** on a page to set:

- **Owner** — who is responsible for keeping this page correct. They receive the reminders.
- **Review every (days)** — how often it should be checked. Leave it empty for no cycle.

Setting an interval starts the clock today. When the date passes, the page shows *This page is due
for review*, the owner is notified, and it appears on their dashboard and on the **Review due**
screen.

### Confirming a review

Read the page, check it is still correct, and press **Confirm reviewed**. That resets the clock.

If it is *not* correct, fix it first. Editing does not silently clear the flag — you still confirm
when you are satisfied.

### What to put an interval on

Anything whose wrongness would cost you something: runbooks, contact lists, procedures with
external dependencies, anything referencing a version number. Reference material that does not
change does not need a cycle, and a wiki where everything is overdue teaches people to ignore the
banner.

### The Review due screen

Lists every overdue page you can see, with its owner and how many days late it is. Pages with no
reachable owner are shown as **Unassigned** — those are the ones that quietly rot, so they are
worth a pass.

You can export the list as CSV.

## Acknowledgments

Some pages have to be *read*, not just published — a security policy, a changed procedure, an
on-call rota.

Turn on **Requires acknowledgment** in the same panel. Everyone who can read the page is then asked
to confirm they have.

### As a reader

The page shows *You must confirm you have read this page*. Read it, then press **I have read
this**. Your confirmation records **the exact version you read**, so there is a real answer to "what
did they actually agree to".

Unconfirmed pages appear on your dashboard, and turn overdue if you leave them.

### As the page owner

The **Acknowledgments** report shows who has confirmed and who has not, with progress, and exports
as CSV.

When you make a change significant enough that people need to read it again, mark it a **material
revision**. That asks everyone again. Fixing a typo should not.
