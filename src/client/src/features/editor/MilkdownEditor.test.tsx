import { createRef } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

import { MilkdownEditor, type MilkdownHandle } from './MilkdownEditor';

/**
 * The selection handle, round-tripped through ProseMirror rather than through the DOM.
 *
 * @remarks
 * <p>
 * This is the highest-risk code in the AI work and the only part of it whose failure is silent. If
 * `selectedMarkdown` returned rendered text instead of Markdown, "Improve selection" would send a
 * model a paragraph with every bold, link and list marker already stripped — and then paste the
 * answer back over the original, flattening formatting the user never touched. Nothing would error.
 * The page would just quietly get worse.
 * </p>
 * <p>
 * So the assertion is deliberately not "some text came back": it is that the emphasis markers
 * survived. `**bold**` proves the serializer ran; `bold` alone would prove `textContent` did.
 * </p>
 */
describe('MilkdownEditor selection handle', () => {
  afterEach(cleanup);

  const document1 = 'First paragraph.\n\nSecond **bold** paragraph.\n';

  async function mount() {
    const handle = createRef<MilkdownHandle>();
    let latest = '';

    render(
      <MantineProvider>
        <MilkdownEditor
          value={document1}
          onChange={(markdown) => {
            latest = markdown;
          }}
          pagePath=""
          handleRef={handle}
        />
      </MantineProvider>,
    );

    const root = await waitFor(() => {
      const node = window.document.querySelector('.ProseMirror');
      expect(node).toBeTruthy();
      return node as HTMLElement;
    }, { timeout: 10_000 });

    return { handle, root, read: () => latest };
  }

  /**
   * Highlights one whole paragraph, the way a user dragging across a line would.
   *
   * The `focus()` is not decoration: ProseMirror's DOM observer only reads a selection change back
   * into its own state while the view has focus, so without it the editor stays at an empty
   * selection and every assertion below would be about nothing.
   */
  function selectParagraph(root: HTMLElement, index: number) {
    const paragraph = root.querySelectorAll('p')[index];
    expect(paragraph).toBeTruthy();

    root.focus();

    const range = window.document.createRange();
    range.selectNodeContents(paragraph!);

    const selection = window.getSelection();
    selection?.removeAllRanges();
    selection?.addRange(range);

    window.document.dispatchEvent(new Event('selectionchange'));
  }

  it('serializes the highlighted range back to Markdown, markers and all', async () => {
    const { handle, root } = await mount();

    selectParagraph(root, 1);

    await waitFor(() => {
      const selected = handle.current?.selectedMarkdown() ?? '';
      expect(selected).toContain('Second');
    }, { timeout: 5000 });

    const selected = handle.current!.selectedMarkdown();

    // The whole point. Reading the DOM would have given "Second bold paragraph."
    expect(selected).toContain('**bold**');
    expect(selected).not.toContain('First paragraph.');
  });

  it('returns nothing when there is no highlight, so the action falls back to the page', async () => {
    const { handle, root } = await mount();

    const selection = window.getSelection();
    selection?.removeAllRanges();

    const range = window.document.createRange();
    range.setStart(root, 0);
    range.collapse(true);
    selection?.addRange(range);
    window.document.dispatchEvent(new Event('selectionchange'));

    expect(handle.current?.selectedMarkdown()).toBe('');
  });

  /**
   * Replacing has to touch the highlighted range and nothing else — the failure that matters is a
   * paragraph-sized rewrite landing over the whole document.
   */
  it('replaces only the highlighted range, leaving the rest of the document alone', async () => {
    const { handle, root, read } = await mount();

    selectParagraph(root, 1);

    await waitFor(() => expect(handle.current?.selectedMarkdown()).toContain('Second'), { timeout: 5000 });

    handle.current!.replaceSelection('A tidier sentence.');

    await waitFor(() => expect(read()).toContain('A tidier sentence.'), { timeout: 5000 });

    const after = read();
    expect(after).toContain('First paragraph.');
    expect(after).not.toContain('Second');
  });

  /** A handle used before the editor finished mounting must not throw into the page. */
  it('is inert rather than fatal when the editor is not ready', () => {
    const handle = createRef<MilkdownHandle>();

    render(
      <MantineProvider>
        <MilkdownEditor value={document1} onChange={() => undefined} pagePath="" handleRef={handle} />
      </MantineProvider>,
    );

    expect(handle.current?.selectedMarkdown()).toBe('');
    expect(() => handle.current?.replaceSelection('anything')).not.toThrow();
  });
});
