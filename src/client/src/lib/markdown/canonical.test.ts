import { describe, expect, it } from 'vitest';
import { canonicalize, isCanonical, splitFrontMatter } from './canonical';

/**
 * Criteria 3–5, which belong here because remark in the client is the only writer of Markdown in
 * the product.
 *
 * The corpus is small and deliberate rather than generated: nested lists, tables with inline
 * formatting, fenced code, callouts, accented Spanish front matter, CRLF, and the constructs that
 * broke in the spike. A generated fixture is a fixture built to pass.
 */
const CORPUS: { name: string; markdown: string }[] = [
  {
    name: 'headings and paragraphs',
    markdown: '# Título\n\nUn párrafo con **negrita** y *cursiva*.\n\n## Sección\n\nOtro párrafo.\n',
  },
  {
    name: 'nested lists',
    markdown: '- Uno\n  - Uno punto uno\n  - Uno punto dos\n- Dos\n\n1. Primero\n2. Segundo\n',
  },
  {
    name: 'task list',
    markdown: '- [ ] Revisar el certificado\n- [x] Reiniciar el servicio\n',
  },
  {
    name: 'table with inline formatting',
    markdown: '| Campo | Valor |\n| ----- | ----- |\n| IP    | `192.168.1.1` |\n| Sitio | **VPN-Site-A** |\n',
  },
  {
    name: 'fenced code with a language hint',
    markdown: '```bash\nip route add 10.0.0.0/8 via 192.168.1.1\n```\n',
  },
  {
    name: 'mermaid diagram',
    markdown: '```mermaid\ngraph TD;\n  A-->B;\n```\n',
  },
  {
    name: 'blockquote',
    markdown: '> Esto es una cita.\n>\n> Con dos párrafos.\n',
  },
  {
    name: 'links and images',
    markdown: 'Ver [el manual](https://example.com/manual) y ![diagrama](assets/diagrama.png).\n',
  },
  {
    name: 'thematic break',
    markdown: 'Antes.\n\n---\n\nDespués.\n',
  },
  {
    name: 'accented Spanish front matter',
    markdown: '---\ntitle: Política de teletrabajo\nlang: es\n---\n\n# Política\n\nContenido con ñ y acentos.\n',
  },
  {
    name: 'front matter with unknown keys',
    markdown: '---\ntitle: Runbook\nconfluenceId: 12345\ncustomField: keep me\n---\n\nCuerpo.\n',
  },
  {
    name: 'long line, never hard-wrapped',
    markdown: `${'palabra '.repeat(60).trim()}\n`,
  },
];

describe('canonical Markdown', () => {
  it.each(CORPUS)('is idempotent for $name', ({ markdown }) => {
    const once = canonicalize(markdown);
    const twice = canonicalize(once);

    // Criterion 3: canonical input round-trips byte for byte.
    expect(twice).toBe(once);
    expect(isCanonical(once)).toBe(true);
  });

  it.each(CORPUS)('preserves front matter verbatim for $name', ({ markdown }) => {
    const { frontMatter } = splitFrontMatter(markdown);
    const canonical = canonicalize(markdown);

    if (frontMatter) {
      // Unknown keys survive, which is the no-lock-in promise in one assertion.
      expect(canonical.startsWith(frontMatter)).toBe(true);
    }
  });

  /** Criterion 5: a one-word edit in a long canonical page changes at most two lines. */
  it('confines a one-word edit to the lines that word is on', () => {
    const original = canonicalize(
      Array.from({ length: 300 }, (_, i) => `Línea número ${i} de un documento razonablemente largo.`).join('\n\n') +
        '\n',
    );

    const edited = canonicalize(original.replace('número 150', 'número ciento cincuenta'));

    const changed = countChangedLines(original, edited);
    expect(changed).toBeLessThanOrEqual(2);
  });

  it('normalizes non-canonical input once, then leaves it alone', () => {
    // Setext heading, `*` bullets and an indented code block — all valid Markdown, none canonical.
    const messy = 'Título\n======\n\n* uno\n* dos\n\n    código indentado\n';

    const first = canonicalize(messy);
    const second = canonicalize(first);

    expect(first).not.toBe(messy);
    expect(second).toBe(first);
    expect(first).toContain('# Título');
    expect(first).toContain('- uno');
  });

  /**
   * The regression: press Enter above the heading of a new page, type, save — and the reader gets a
   * literal `<br />` on the first line, because Milkdown serializes an empty paragraph as an HTML
   * break and the renderer escapes raw HTML instead of rendering it.
   */
  it('drops the <br /> Milkdown writes for a blank line', () => {
    const fromEditor = '---\ntitle: Test page\nlang: en\n---\n<br />\n\n# Title\n\nHey! this is my first page\n';

    const canonical = canonicalize(fromEditor);

    expect(canonical).not.toContain('<br');
    expect(canonical).toBe('---\ntitle: Test page\nlang: en\n---\n# Title\n\nHey! this is my first page\n');
    expect(canonicalize(canonical)).toBe(canonical);
  });

  it.each(['<br>', '<br/>', '<br />', '<BR />'])('drops a blank-line %s wherever it appears', (tag) => {
    expect(canonicalize(`# Uno\n\n${tag}\n\n## Dos\n\n${tag}\n\nFinal.\n`)).toBe('# Uno\n\n## Dos\n\nFinal.\n');
  });

  it('keeps a <br /> a person typed between words', () => {
    // Escaped rather than rendered, which is the documented HTML policy — but it is the author's
    // text, and silently deleting it would be the worse bug.
    const inline = 'Primera línea<br />segunda línea.\n';

    expect(canonicalize(inline)).toContain('<br />');
  });

  it('leaves a document with no front matter alone', () => {
    expect(splitFrontMatter('# Hola\n')).toEqual({ frontMatter: null, content: '# Hola\n' });
  });

  it('does not treat an unterminated block as front matter', () => {
    const text = '---\ntitle: nunca se cierra\n\ncuerpo\n';
    expect(splitFrontMatter(text).frontMatter).toBeNull();
  });
});

function countChangedLines(before: string, after: string): number {
  const left = before.split('\n');
  const right = after.split('\n');
  let changed = 0;

  for (let i = 0; i < Math.max(left.length, right.length); i++) {
    if (left[i] !== right[i]) {
      changed++;
    }
  }

  return changed;
}
