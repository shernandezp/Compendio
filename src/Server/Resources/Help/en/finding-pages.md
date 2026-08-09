# Finding pages

There are three ways to find something, and they are good at different things.

## The search box — searching inside pages

The box at the top searches the **full text** of every page you can read: titles, headings, body
text, tags and paths. It is not a page-name lookup.

Type and press Enter. Results show the page title, where it lives, a snippet with your words
highlighted, its tags and when it was last updated.

### Search syntax

You can type plain words and it will work. When you need to be precise:

| What you type | What it does |
|---|---|
| `vpn cisco` | Finds pages containing **both** words |
| `"site to site"` | Finds that **exact phrase** |
| `-obsolete` | **Excludes** pages containing that word |
| `tag:security` | Only pages with that tag |
| `in:IT/VPN` | Only pages under that folder |
| `owner:ana` | Only pages owned by that person |
| `lang:es` | Only pages in that language |
| `updated:>2026-01-01` | Only pages changed after that date |

These combine: `firewall in:IT -draft` finds firewall pages under *IT* that do not mention *draft*.

### Things it does for you

- **Accents do not matter.** Searching `sesion` finds *sesión*, and `politica` finds *política*.
- **The last word is completed for you.** Typing `servidor` also finds *servidores*, so results
  appear while you are still typing.
- **Your language ranks first.** When a page exists in two languages, the one matching your
  interface language is listed higher — but the other is still there.

### If you get nothing

Try fewer words. Search requires *all* of them, so a long query is a narrow one. Check the spelling,
and remember that a page you cannot read will never appear — including in the result count.

## Ctrl-K — jumping to a page you already know

Press **Ctrl-K** (**Cmd-K** on a Mac) and start typing a page name. This is the fastest route when
you know what the page is called and just want to open it. It tolerates partial and slightly
misspelled names.

## The tree and tags — browsing

The left panel is the folder tree, which usually mirrors how your organization is organized.

The **Tags** screen lists every tag in use with a count, and clicking one lists its pages. Counts
are calculated for you specifically, so a tag never reveals pages you cannot open.

**Recently updated** is a good way to see what has been changing lately.

## A note on the index

Search is powered by an index that is rebuilt automatically as pages change. Just after a large
import or a rebuild you may briefly see a notice that results could be incomplete. It clears on its
own.
