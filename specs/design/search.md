# Design Note — Search

> Status: Draft v0.1 — 2026-07-30
> Expands `project-overview.md` §5.1 ("Full-text search via SQLite FTS5") and answers open question §10.5.
> Related: [`permissions.md`](permissions.md) — search is the easiest place to leak; [`secure-content.md`](secure-content.md) §6.2; [`localization.md`](localization.md).

---

## 1. Goals

- Instant (p95 under 150 ms), in-process, no external engine — ever. If FTS5 turns out to be too slow at a size we actually reach, the fallback is a different local index, never Elasticsearch (§4.2).
- **Correct under permissions.** A user must never learn that a page exists from search: not from a title, a snippet, a result count, an autocomplete suggestion, a tag count, or a backlink.
- Works for Spanish and English content in the same index, in the same query.
- A pure cache. `compendio reindex` rebuilds it from the content folder at any time, and deleting it costs nothing but a rebuild (§4.1).

---

## 2. Schema

```sql
-- Metadata: authoritative row per page, mirrors the file.
CREATE TABLE pages (
  id INTEGER PRIMARY KEY,
  path TEXT NOT NULL UNIQUE,        -- content-relative, forward slashes
  folder_id INTEGER NOT NULL REFERENCES folders(id),
  title TEXT NOT NULL,
  lang TEXT,                        -- BCP-47, from front matter
  translation_key TEXT,
  tags TEXT,                        -- normalized, space-separated
  owner TEXT,
  updated_at TEXT NOT NULL,         -- ISO-8601 UTC
  content_hash TEXT NOT NULL,
  is_secure INTEGER NOT NULL DEFAULT 0
);

-- External-content FTS: the index stores no copy of the metadata.
CREATE VIRTUAL TABLE pages_fts USING fts5(
  title, headings, body, tags, path,
  content='pages_text', content_rowid='page_id',
  tokenize = "unicode61 remove_diacritics 2 tokenchars '_-.'"
);
```

`pages_text` holds the extracted plain text per page (Markdown stripped of syntax: front matter removed, code fences kept as text, link targets dropped but link text kept, image alt text kept). Keeping the extraction in its own table makes `snippet()` cheap and makes reindexing a pure function of the file.

### Ranking

BM25 with column weights — `bm25(pages_fts, 10.0, 4.0, 1.0, 6.0, 2.0)` (title, headings, body, tags, path) as a starting point, tunable in config so it can be adjusted against real content without a release. Post-multipliers applied in SQL: a small recency boost (pages updated in the last 90 days), and a **language boost** for pages whose `lang` matches the user's resolved UI language (`localization.md` §2) so the Spanish version of a bilingual pair ranks above the English one for a Spanish user, without hiding the other.

---

## 3. Tokenization — the Spanish decision

`unicode61 remove_diacritics 2` — the accent-folding option that also handles combining marks correctly. This makes `sesion` match *sesión*, `politica` match *política*, `anos` match *años* (and yes, also *anos* — an acceptable trade for a search box).

**No stemming.** SQLite ships only the English `porter` stemmer; using it would degrade Spanish while helping English only slightly, and mixed-language corpora make a single stemmer wrong by construction. Spanish plural/verb forms therefore do not conflate (`servidor` ≠ `servidores`). Mitigations, in order of cost:

1. Prefix matching on the last term of a query by default (`servidor*` matches `servidores`) — cheap, covers most of the gap, standard search-box behaviour.
2. A per-instance synonym list in config (`servidor = servidores = server`) applied at query time — a hundred lines, and it lets an org fix its own vocabulary.
3. A proper Spanish snowball stemmer as a custom tokenizer — deferred; revisit only if real usage shows it matters.

`tokenchars '_-.'` keeps `192.168.1.1`, `VPN-Site-A` and `snake_case_names` searchable as units, which for an IT wiki is not a detail. A secondary **trigram** index on titles and paths only (small) powers substring/typo-tolerant lookups in the quick-switcher without bloating the main index.

---

## 4. Permission filtering — filter *in* the query

The rule: **the permission predicate is part of the SQL, never a filter applied to results afterwards.** Post-filtering breaks pagination (page 2 of 10 results becomes page 2 of 3), leaks totals, and produces empty pages that themselves reveal that hidden matches exist.

```
readable = evaluator.ReadableFolderIds(user)     // from permissions.md §4, cached per (user, aclVersion)

SELECT p.path, p.title, snippet(pages_fts, 2, '<mark>', '</mark>', '…', 12) AS excerpt,
       bm25(pages_fts, 10,4,1,6,2) AS rank
FROM pages_fts
JOIN pages p ON p.id = pages_fts.rowid
WHERE pages_fts MATCH :query
  AND p.folder_id IN (SELECT folder_id FROM readable_folders WHERE user_id = :uid)
  AND (p.is_secure = 0 OR p.folder_id IN (SELECT folder_id FROM secure_indexed))
ORDER BY rank
LIMIT :limit OFFSET :offset;
```

`readable_folders` is a materialized per-user table, rebuilt lazily on first search after the ACL version changes. Folder counts at SMB scale are in the hundreds, so this is a few dozen rows per user and an index lookup; a literal `IN (...)` list is an acceptable v0 shortcut but the table avoids SQL-length cliffs and makes the join planner-friendly.

**Total counts** are computed with the same predicate, so "12 results" means twelve results *you can see*.

### The other search surfaces

Every one of these is a search index in disguise and every one has leaked in some shipped wiki:

| Surface | Filter |
|---|---|
| Quick switcher / `Ctrl-K` | Same predicate |
| `[[link]]` autocomplete in the editor | Same predicate — otherwise the editor becomes a page-name oracle |
| Backlinks panel | Same predicate; a backlink from a restricted page is invisible |
| Tag browsing and tag counts | Counts recomputed per user, not cached globally |
| "Recently updated", "Most read" | Same predicate |
| AI retrieval / "Ask the wiki" | Same predicate, applied *before* the chunks reach the model — the RAG path must not be a permissions bypass |
| Sitemap / static export | Only what the exporting user can read |

An integration test suite exists specifically for this: for each surface, assert that a restricted page is invisible to an unauthorized user *and* visible to an authorized one. Add a row to that suite whenever a surface is added.

### Secure scopes

Excluded by default (`secure-content.md` §6.2). Only scopes an admin explicitly opted into full-text indexing appear in `secure_indexed`, and their rows live in a separate FTS table so they can be dropped in one statement.

---

## 5. Query syntax

Small, predictable, parsed by us — user input is never passed raw to `MATCH` (an unbalanced quote returning a 500 is a bad look, and FTS5 syntax errors are unhelpful):

| Input | Meaning |
|---|---|
| `vpn cisco` | All terms (implicit AND), last term prefix-matched |
| `"site to site"` | Phrase |
| `-obsolete` | Exclude |
| `tag:seguridad` | Structured filter on `pages.tags` |
| `space:IT` / `in:IT/VPN` | Path prefix |
| `owner:ana` | Structured filter |
| `lang:es` | Structured filter |
| `updated:>2026-01-01` | Structured filter |

The parser splits structured filters out into SQL `WHERE` clauses and hands only the free-text remainder to `MATCH`, properly quoted. Unknown `foo:bar` prefixes are treated as literal text rather than an error.

Results show title, breadcrumb path, a highlighted snippet, tags, language badge (when the page is not in the user's language), and updated date. Escape the snippet as HTML — `snippet()` returns page content, and page content contains `<script>` sometimes.

---

## 6. Index maintenance

- **Incremental**, driven by the file watcher: change → debounce 500 ms → re-extract → upsert `pages` + `pages_text` → FTS update, inside one transaction.
- A durable `index_queue` table means a crash mid-batch resumes rather than silently leaving stale rows; startup drains it and reconciles by comparing `content_hash` against the file system.
- **Full rebuild:** `compendio reindex` (online, in batches, with progress), `--drop-secure` to purge opted-in secure content.
- Folder rename/move updates paths in one transaction with the ACL move (`permissions.md` §5) — search and permissions must never disagree about where a page lives.
- `/readyz` reports index status (`ready` / `rebuilding N%` / `stale`), and the UI shows a quiet banner while a rebuild is in progress rather than returning wrong results silently.

## 7. What is indexed

In: page body, title, headings, tags, path, attachment **file names**.
Out (v1): attachment contents (PDF/DOCX text extraction — a real feature, but it needs a parsing library and belongs in its own decision), page history (only current versions are searchable), and comments (they do not exist yet).

## 8. Measuring scale — closing open question §10.5

No synthetic corpus and no generator. The question "is FTS5 fast enough" gets answered against **our own content during dogfooding**, because a fixture built to exercise search is a fixture built to pass it, and the real distribution of page sizes, tag density and folder shape is the thing that decides the answer.

What gets recorded, each time, with the machine and the corpus size written down beside it: cold and warm p50/p95 for a single term, two terms, a phrase, a prefix, a tag-filtered query, and a permission-restricted user who can see a small fraction of the tree. Plus index rebuild time and database size relative to the content folder.

Targets are in the implementation spec, and they are targets — the actionable signal is a release that is slower than the previous one on the same machine and the same content.

If FTS5 does fall short at a size we actually reach, the recorded fallbacks are: trigram-only titles plus a body scan for rare queries, or a local Lucene-style index. **Never an external service** (§4.2).

## 9. Open questions

1. Should zero-result queries be logged for the "what documentation is missing" analytics idea (§5.3)? It is genuinely useful and it is also a log of what people search for. Proposal: opt-in, admin-visible, aggregate-only, no user attribution.
2. Highlight in the opened page (`?q=` scroll-to-match)? Cheap and much liked. Proposal: yes, MVP if it fits.
3. Do we need per-space relevance tuning, or is one global weight set enough? Proposal: one global set, revisit with real usage.
