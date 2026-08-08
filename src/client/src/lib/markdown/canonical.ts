import { unified } from 'unified';
import remarkParse from 'remark-parse';
import remarkStringify from 'remark-stringify';
import remarkGfm from 'remark-gfm';
import type { Options as StringifyOptions } from 'remark-stringify';

/**
 * Canonical Markdown.
 *
 * This file is the single definition of how Compendio spells a document, and it changes only by an
 * explicit decision — not by upgrading a dependency and accepting whatever it now emits.
 *
 * The reasoning it encodes: no ProseMirror-based editor can promise byte-identical round trips
 * against arbitrary Markdown, because the same document has many valid spellings. So instead of an
 * unachievable "never reformat", there is one canonical spelling and reformatting becomes a single
 * visible event — the first time a human saves a page in the editor. After that the invariant is
 * idempotence, which is testable at 100 % rather than aspirationally at 95 %.
 */
export const CANONICAL_OPTIONS: StringifyOptions = {
  /** ATX headings (`## Title`), never Setext underlines. */
  setext: false,
  /** `-` for bullets. */
  bullet: '-',
  bulletOther: '*',
  /** `1.` for ordered lists, and numbers that do not renumber the whole list on an insert. */
  listItemIndent: 'one',
  incrementListMarker: true,
  /** Fenced code with a language hint, never indented code blocks. */
  fence: '`',
  fences: true,
  /** `*` for emphasis and `**` for strong. */
  emphasis: '*',
  strong: '*',
  /** No hard wrapping: a wrapped paragraph turns a one-word edit into a whole-paragraph diff. */
  bulletOrdered: '.',
  rule: '-',
  ruleRepetition: 3,
  ruleSpaces: false,
  tightDefinitions: true,
  quote: '"',
  resourceLink: false,
};

/** The minimum of mdast this file needs. Declaring it here avoids a dependency on `@types/mdast`. */
interface MdastNode {
  type: string;
  value?: string;
  children?: MdastNode[];
}

/** `<br>`, `<br/>`, `<br />` — the spellings Milkdown emits, and the ones a person types. */
const LINE_BREAK_ONLY = /^<br\s*\/?>$/i;

function isLineBreakOnly(node: MdastNode | undefined): boolean {
  return node?.type === 'html' && LINE_BREAK_ONLY.test((node.value ?? '').trim());
}

/**
 * A `<br />` alone on its line, which Milkdown writes for an empty paragraph.
 *
 * The editor's `remark-preserve-empty-line` plugin serializes a blank line as an HTML break so that
 * pressing Enter twice survives a round trip. Compendio cannot accept that: the renderer runs Markdig
 * with `DisableHtml`, so raw HTML is escaped rather than rendered and the reader sees the literal
 * text `<br />` at the top of the page. `html-block` is already on {@link UNREPRESENTABLE_CONSTRUCTS}
 * for exactly this reason — this is the one place that produced it anyway.
 *
 * Only a break that is the whole block goes. A `<br />` between words in a paragraph is something a
 * person typed, and deleting content nobody asked us to delete is the worse failure.
 */
function isBlankLineArtifact(node: MdastNode, parent: MdastNode): boolean {
  if (parent.type === 'paragraph') {
    return false;
  }

  return (
    isLineBreakOnly(node) ||
    (node.type === 'paragraph' && node.children?.length === 1 && isLineBreakOnly(node.children[0]))
  );
}

function pruneBlankLineArtifacts(node: MdastNode): void {
  if (!node.children) {
    return;
  }

  node.children = node.children.filter((child) => !isBlankLineArtifact(child, node));
  node.children.forEach(pruneBlankLineArtifacts);
}

function remarkDropBlankLineArtifacts() {
  return (tree: MdastNode) => pruneBlankLineArtifacts(tree);
}

const processor = unified()
  .use(remarkParse)
  .use(remarkGfm, { singleTilde: false })
  .use(remarkDropBlankLineArtifacts)
  .use(remarkStringify, CANONICAL_OPTIONS);

/**
 * Parses and re-serializes. For canonical input this returns identical bytes; for anything else it
 * returns the canonical spelling of the same document.
 */
export function canonicalize(markdown: string): string {
  const body = splitFrontMatter(markdown);
  const serialized = String(processor.processSync(body.content));

  // Front matter is passed through verbatim. remark does not own it, and rewriting it would drop
  // the unknown keys that other tools put there — which is the no-lock-in promise, in one line.
  return body.frontMatter ? `${body.frontMatter}${serialized}` : serialized;
}

/** True when the text is already in canonical form. */
export function isCanonical(markdown: string): boolean {
  return canonicalize(markdown) === markdown;
}

/**
 * Splits the YAML front-matter block off the top.
 *
 * Deliberately textual rather than a remark plugin: the block has to survive untouched, and the
 * cheapest way to guarantee that is never to parse it here at all.
 */
export function splitFrontMatter(markdown: string): { frontMatter: string | null; content: string } {
  if (!markdown.startsWith('---')) {
    return { frontMatter: null, content: markdown };
  }

  const firstNewline = markdown.indexOf('\n');
  if (firstNewline < 0 || markdown.slice(3, firstNewline).trim() !== '') {
    return { frontMatter: null, content: markdown };
  }

  let index = firstNewline + 1;
  while (index < markdown.length) {
    const lineEnd = markdown.indexOf('\n', index);
    const end = lineEnd < 0 ? markdown.length : lineEnd;

    if (markdown.slice(index, end).trimEnd() === '---') {
      const after = lineEnd < 0 ? markdown.length : lineEnd + 1;
      return { frontMatter: markdown.slice(0, after), content: markdown.slice(after) };
    }

    if (lineEnd < 0) {
      break;
    }

    index = lineEnd + 1;
  }

  // Unterminated: not front matter, so the whole file is content.
  return { frontMatter: null, content: markdown };
}

/**
 * Constructs that the canonical serializer cannot round-trip.
 *
 * The milestone-0 spike produces this list, and anything on it is removed from the toolbar — a
 * feature we do not ship rather than a feature that silently damages a document.
 */
export const UNREPRESENTABLE_CONSTRUCTS: readonly string[] = [
  'html-block',
  'html-inline',
  'definition-list',
  'footnote-in-table-cell',
];
