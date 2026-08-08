# Design Note — Localization (Spanish + English)

> Status: Draft v0.1 — 2026-07-30
> Expands `project-overview.md` §3 differentiator 5 ("Bilingual-friendly") into an implementable spec.
> Related: [`search.md`](search.md) (diacritics and per-language ranking).

---

## 1. Two separate problems

They get conflated constantly, and conflating them produces a wiki where the buttons are in Spanish and the SOPs are in English with no way to say so.

| | **UI localization** | **Content localization** |
|---|---|---|
| What | Buttons, menus, errors, wizard, emails-that-don't-exist, CLI output | The pages themselves |
| Who supplies it | Us, shipped in the binary | The organization, as it writes |
| v0 scope | **Full: English + Spanish, complete, from the first release** | Language tagging + translation linking + a language switcher |
| v1 scope | Community locales accepted | AI-assisted translation (§5.2) creating a linked draft |

Launching bilingual is a positioning decision, not a nicety: the target user is an SMB in a Spanish-speaking country whose IT admin reads English fine and whose HR manager does not. Both must be able to use the product.

---

## 2. UI localization

### Stack

- **Frontend:** `i18next` + `react-i18next`. Mature, framework-agnostic core, plural rules for `es`/`en` built in, namespace splitting, and a well-trodden lint story. Catalogs are plain JSON, so a translator with no tooling can work on them.
- **Backend:** `IStringLocalizer` over `.resx`, for the small set of strings that genuinely originate server-side: API error messages returned to the user, validation messages, setup-wizard bootstrap text, CLI output, `compendio doctor` findings.

Everything that *can* be localized on the client, is. The server-side set stays deliberately small.

### Catalog layout

```
src/Compendio.Web/src/locales/
├── en/  common.json  nav.json  editor.json  admin.json  search.json  errors.json
└── es/  common.json  nav.json  editor.json  admin.json  search.json  errors.json
```

Keys are semantic paths, never English sentences: `editor.toolbar.insertTable`, not `"Insert table"`. English-as-key makes every copy tweak a breaking change across all locales.

### Resolution order

1. Explicit user preference (profile setting — wins over everything, and is the answer to "the browser is in English but I want Spanish").
2. `?lang=` query parameter (for sharing a link in a specific language, and for testing).
3. `compendio_lang` cookie (set by 1 and 2).
4. `Accept-Language`, matched with BCP-47 fallback (`es-MX` → `es`).
5. Instance default, chosen in the setup wizard.
6. `en`.

The same chain runs server-side so an API error is returned in the same language the SPA is rendering.

### Rules that prevent the usual mess

- **The server never formats a date, number, or currency for display.** It returns ISO-8601 UTC (`2026-07-30T14:03:00Z`) and raw numbers; the client formats with `Intl.DateTimeFormat` / `Intl.NumberFormat` / `Intl.RelativeTimeFormat` in the resolved locale. This one rule removes an entire category of bug and makes "hace 3 días" free.
- **No string concatenation.** Interpolation and plurals go through i18next (`t('search.results', { count })`), because Spanish plural and gender agreement do not survive `"Found " + n + " results"`.
- **No hardcoded user-facing strings.** Enforced by an ESLint rule (`i18next/no-literal-string`) scoped to `src/components/**` and `src/pages/**`, in CI.
- **No `left`/`right` in CSS** — use logical properties (`margin-inline-start`, `padding-block`). Costs nothing now, and is the only thing that makes an Arabic or Hebrew community locale possible later without a rewrite.
- **Layout tolerates ~35 % text expansion.** Spanish is reliably longer than English; a pseudo-locale (`en-XA`, generated at build time, wraps and lengthens every string) is available via `?lang=en-XA` to find the buttons that will overflow.
- **The setup wizard is localized, and its first control is the language picker** — before the admin account screen. A wizard that is English-only sets the tone before the product is even installed.
- **Logs stay in English, always.** Ops greppability and pasteable GitHub issues beat localized logs. User-facing CLI output (`install`, `doctor`, `backup`) *is* localized, from the OS locale, with `--lang` to override.

### Translation completeness in CI

A test asserts key-set parity between `en` and `es` (missing keys fail the build; extra keys warn). i18next's fallback to `en` stays enabled as a runtime safety net, but a missing key is treated as a bug, not a shrug.

---

## 3. Content localization

### Tagging a page's language

Front matter is authoritative:

```yaml
---
title: Política de teletrabajo
lang: es
translationKey: hr-remote-work-policy
translationOf: HR/Policies/remote-work.md   # optional, informative
---
```

- `lang` — BCP-47. Absent means the instance default language.
- `translationKey` — a stable identifier shared by all language versions of the same document. Path-based linking breaks the moment someone reorganizes folders; a key does not.

### File convention

Translations live **beside** the original: `remote-work.md` (default language) and `remote-work.es.md`. The suffix is a convention the UI and importers follow, and a *fallback* the parser uses when front matter is missing — front matter always wins if both are present. Sibling files keep the folder tree intact, keep permissions identical for all translations (they share a folder), and keep the diff of a translation next to its source.

Rejected alternative: parallel language trees (`content/es/HR/...`). It doubles the navigation tree, splits permissions across two paths for the same document, and makes "which pages have no Spanish version" a tree-diff instead of a query.

### Behaviour

- A page with siblings shows a language switcher; switching preserves scroll position and the anchor.
- If a user's UI language has no version of the page, show the available one with an unobtrusive banner: *"This page is only available in English."* Never a 404, never a blank.
- **Translation staleness:** when the source page is edited, linked translations get a "source has changed" flag on the page and in the owner dashboard, reusing the §5.2 lifecycle machinery. This is the feature that keeps a bilingual wiki honest.
- v1 AI *Translate page* creates the sibling file as a **draft** with `translationKey` prefilled and a visible "machine-translated, unreviewed" badge that clears on human save. A wrong Spanish HR policy is worse than no Spanish HR policy.

### Slugs and file names

Titles keep their accents and ñ; **file names are ASCII-slugified** (`política-de-teletrabajo` → `politica-de-teletrabajo`). This is a Windows/SMB path-safety and cross-platform decision (§6 "File naming"), not an aesthetic one — the title in front matter carries the real text and is what the UI shows everywhere.

---

## 4. Acceptance criteria

1. The entire UI, including the setup wizard, admin screens, editor, error states and empty states, renders fully in Spanish and in English with no English leakage in `es`.
2. Switching language never reloads a page's content or loses editor state.
3. CI fails on a missing `es` key and on a hardcoded literal in a component.
4. Search for `sesion` finds a page titled *"Configuración de sesión"* (see `search.md` §5).
5. A page and its Spanish sibling appear once each in search results, with the user's language ranked first.
6. `?lang=en-XA` shows every string wrapped and lengthened, with no clipped or overflowing controls.

---

## 5. Open questions

1. Do we ship a third locale at launch to prove the model generalizes (e.g. `pt-BR` or `ca`)? Proposal: no — two, done properly, and a documented contribution path.
2. Should the instance be able to *force* a single UI language for all users (some orgs want uniformity)? Proposal: yes, a config flag; cheap and occasionally demanded.
3. Language-specific navigation ordering (Spanish alphabetization of ñ, accents). Proposal: sort with `Intl.Collator` in the resolved locale on the client; the API returns unsorted-but-stable order.
