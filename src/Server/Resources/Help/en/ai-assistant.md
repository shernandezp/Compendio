# The AI assistant

**Optional.** If your administrator has not configured a provider, none of this appears anywhere in
the product — no greyed-out buttons, no upsell. If you cannot see the controls described here, that
is why.

Where it is enabled, every AI control tells you which endpoint your text is being sent to. If that
endpoint is a model running on your own server, nothing leaves the building.

## Ask the wiki

The question-mark icon next to the search box. Ask a question in ordinary language and get an
answer, with links to the pages it came from.

**This is not the search box, and it does not replace it.** The two do different jobs:

| | Search | Ask the wiki |
|---|---|---|
| Gives you | pages to read | an answer, plus its sources |
| Best for | finding a document | a question whose answer is spread across several pages |
| Always correct about what exists | yes | it can be wrong — check the sources |
| Costs | nothing | one request from your daily allowance |

It only ever reads pages **you** can read. And if nothing in those pages answers your question, it
says so rather than inventing an answer from general knowledge.

## While writing

In the editor, under the **AI** menu:

- **Improve writing** — tidies the whole page, or just your selection.
- **Summarize** — a summary of the page or the selection.
- **Check for outdated content** — flags things that look stale: old version numbers, dates that
  have passed, references to systems that may be gone.
- **Draft from notes** — the useful one. Type what you know as rough bullet points, in any order,
  and get a structured page back. Most people find this easier than a blank page.

Everything is a **proposal**. You see the suggestion, and nothing changes until you press
**Use this**. Nothing is saved until you save the page.

## Translating

**Translate this page** produces a translation in another language as a new page. It is badged
**machine-translated, unreviewed** until a person opens it and saves it — so a reader always knows
whether a human has checked it.

## Your daily allowance

Because a hosted endpoint costs money per request, there is a cap per person per day, and possibly
one for the whole instance. The editor shows how many requests you have left and roughly when more
become available.

One question to *Ask the wiki* counts as one request, even though it does more than one thing
behind the scenes.

## What it is not

It cannot change a page on its own, it cannot see pages you cannot see, and it does not learn from
your wiki between questions. It reads the pages relevant to what you asked, answers, and forgets.
