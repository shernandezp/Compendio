# Translating Compendio

**A language is data.** Adding one means adding files, not a pass over every screen. If you find
yourself editing a component to make a translation work, something is wrong and we would like to
hear about it.

Compendio ships Spanish and English, both complete. English is the reference text that translations
are made from.

---

## Two different problems

These get conflated constantly, and conflating them produces a wiki where the buttons are in Spanish
and the procedures are in English with no way to say so.

| | **UI localization** | **Content localization** |
|---|---|---|
| What | Buttons, menus, errors, the wizard, CLI output | The pages themselves |
| Who supplies it | Us, shipped in the binary | Your organization, as it writes |
| Covered by | This document | [The `lang` and `translationKey` front matter](#tagging-a-pages-language) |

---

## Adding a UI language

Four files, and none of them is a component.

### 1. The client catalog

Copy `src/client/src/i18n/locales/en.json` to `<code>.json` and translate the values.

```json
{
  "page": {
    "edit": "Editar",
    "history": "Historial"
  }
}
```

Keys are semantic paths — `editor.toolbar.insertTable`, never `"Insert table"`. English-as-key makes
every copy tweak a breaking change across every locale.

Register it in `src/client/src/i18n/index.ts`:

```ts
import ca from './locales/ca.json';

export const SUPPORTED_LANGUAGES = ['es', 'en', 'ca'] as const;

const resources = {
  en: { translation: en },
  es: { translation: es },
  ca: { translation: ca },
} as const;
```

### 2. The server catalog

Copy `src/Server/Resources/Strings.resx` to `Strings.<code>.resx` and translate the `<value>`
elements. This set is deliberately small: `ProblemDetails` titles and details, validation messages,
the first page the wizard writes, and `doctor` output.

### 3. The supported-language list

`src/Server/Domain/Localization/SupportedLanguages.cs`:

```csharp
public static IReadOnlyList<SupportedLanguage> All { get; } =
[
    new(Spanish, "Spanish", "Español"),
    new(English, "English", "English"),
    new("ca", "Catalan", "Català"),
];
```

Every picker in the product renders from this list, so a catalog with no entry here is simply not
offered rather than half-working.

### 4. The satellite assembly list

`src/Server/Compendio.Server.csproj`:

```xml
<SatelliteResourceLanguages>en;es;ca</SatelliteResourceLanguages>
```

**This one is not optional and its failure is silent.** A culture missing from that list is dropped
from the build output entirely — which is how an instance configured for Catalan starts answering in
English, in production, with no error anywhere.

---

## Checking your work

```bash
cd src/client
npm run check:i18n
```

It fails on a key that exists in `en.json` and not in yours, and on any `t('…')` in the source that
does not resolve in every catalog. It warns about extra keys, which are usually leftovers.

This is a CI gate. i18next's runtime fallback to English stays enabled as a safety net, but a
missing key is treated as a bug rather than a shrug.

### Text expansion

Spanish runs about 35 % longer than English, and most other languages run longer still. Outside
Production, `?lang=en-XA` renders a pseudo-locale that wraps and lengthens every string:

```
⟦Save··········⟧
```

Anything that clips or overflows there will clip or overflow in a real translation. One manual pass
per release.

---

## Rules that prevent the usual mess

- **The server never formats a date, a number or a currency for display.** It returns ISO-8601 UTC
  and raw numbers; the client formats with `Intl.DateTimeFormat`, `Intl.NumberFormat` and
  `Intl.RelativeTimeFormat`. This one rule removes a whole category of bug and makes "hace 3 días"
  free.
- **No string concatenation.** Interpolation and plurals go through i18next
  (`t('search.results', { count })`), because Spanish plural and gender agreement do not survive
  `"Found " + n + " results"`.
- **No hardcoded user-facing strings** in components.
- **No `left` or `right` in CSS** — logical properties (`margin-inline-start`, `padding-block`)
  throughout. It costs nothing now and it is the only thing that makes an Arabic or Hebrew locale
  possible later without a rewrite.
- **Logs stay in English, always.** Ops greppability and a pasteable GitHub issue beat a localized
  log line. User-facing CLI output *is* localized, from the OS locale, with `--lang` to override.
- **The setup wizard is localized and its first control is the language picker.** A wizard that is
  English-only sets the tone before the product is even installed.

---

## How the language is chosen

The same chain runs on both sides, so an API error comes back in the language the interface is
already showing:

1. The user's profile preference — wins over everything, and is the answer to "the browser is in
   English but I want Spanish".
2. `?lang=` in the URL, for sharing a link in a particular language and for testing.
3. The `compendio_lang` cookie, set by 1 and 2.
4. `Accept-Language`, with BCP-47 fallback (`es-MX` → `es`).
5. The instance default, chosen in the setup wizard.
6. `en`.

An instance can force one language for everyone with `Instance:ForceSingleLanguage`.

---

## Content localization

Separate from the interface, and the organization's job rather than ours.

### Tagging a page's language

Front matter is authoritative:

```yaml
---
title: Política de teletrabajo
lang: es
translationKey: hr-remote-work-policy
translationOf: HR/Policies/remote-work.md   # optional, informative only
---
```

- `lang` — BCP-47. Absent means the instance default.
- `translationKey` — a stable identifier shared by every language version of the same document.
  Path-based linking breaks the moment somebody reorganizes folders; a key does not.

### The file convention

Translations live **beside** the original: `remote-work.md` and `remote-work.es.md`. Compendio reads
that suffix as a fallback when front matter is missing, and front matter always wins when both are
present.

Siblings rather than parallel language trees (`content/es/HR/…`), because parallel trees double the
navigation tree, split permissions across two paths for the same document, and turn "which pages
have no Spanish version" into a tree diff instead of a query.

### What you get

- A language switcher on any page with siblings.
- If your language has no version of a page, the available one with an unobtrusive banner — never a
  404 and never a blank page.
- A **staleness flag** when the source page is edited after a translation was written. This is the
  feature that keeps a bilingual wiki honest.

### File names stay ASCII

Titles keep their accents and their ñ; file names are slugified (`Política de teletrabajo` →
`politica-de-teletrabajo.md`). That is a Windows/SMB path-safety and cross-platform decision, not an
aesthetic one — the title in front matter carries the real text and is what the interface shows
everywhere.

---

## Contributing a translation

Open a pull request with the four files above. We do not require a native speaker to review it
before merging, but we do mark a community locale as such in the picker until somebody has used it
in anger and said it reads correctly.
