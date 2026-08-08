import { useEffect, useImperativeHandle, useRef, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';
import { Box } from '@mantine/core';

import { api, encodePath } from '../../lib/api';
import { splitFrontMatter } from '../../lib/markdown/canonical';

import '@milkdown/crepe/theme/common/style.css';
import '@milkdown/crepe/theme/frame.css';

/**
 * What the page around the editor can ask of the document without owning it.
 *
 * @remarks
 * Deliberately two methods and no more. The editor stays uncontrolled — that is what keeps the
 * cursor alive between keystrokes — so the alternative to a narrow handle is the page keeping its
 * own copy of the document and the two of them drifting apart.
 */
export interface MilkdownHandle {
  /**
   * The highlighted range, serialized back to Markdown. Empty when nothing is highlighted.
   *
   * @remarks
   * Serialized through ProseMirror rather than read from `window.getSelection()`, which yields the
   * *rendered* text with every emphasis marker, list bullet and link target stripped out. Sending
   * that to a model and accepting the answer would quietly flatten formatting the user never
   * touched — the exact class of silent damage the round-trip rules exist to prevent.
   */
  selectedMarkdown: () => string;

  /** Replaces the highlighted range with parsed Markdown, leaving the rest of the document alone. */
  replaceSelection: (markdown: string) => void;
}

/**
 * The four things this component needs out of Milkdown's context, captured at mount.
 *
 * @remarks
 * Typed through `typeof import(...)` so the runtime import stays dynamic — the editor is the
 * heaviest chunk in the app and must not be pulled into the read view — while the types are still
 * the library's own rather than a hand-written approximation that can drift from it.
 */
interface ProseApi {
  editorViewCtx: (typeof import('@milkdown/kit/core'))['editorViewCtx'];
  serializerCtx: (typeof import('@milkdown/kit/core'))['serializerCtx'];
  parserCtx: (typeof import('@milkdown/kit/core'))['parserCtx'];
  Slice: (typeof import('@milkdown/kit/prose/model'))['Slice'];
}

type CrepeInstance = import('@milkdown/crepe').Crepe;
type Ctx = import('@milkdown/kit/ctx').Ctx;

/**
 * Milkdown, using its Crepe preset.
 *
 * Markdown-native by construction: remark parses and serializes, so the file *is* the document
 * model rather than an export target. That is why it was chosen over BlockNote, whose own
 * documentation calls its Markdown export lossy, and over Tiptap, where Markdown is a community
 * concern and the Pro boundary is a live licensing risk inside an AGPL project.
 *
 * Front matter is held aside and reattached: remark does not own it, and rewriting it would drop
 * the unknown keys other tools put there.
 */
export function MilkdownEditor({
  value,
  onChange,
  pagePath,
  onSelectionChange,
  handleRef,
}: {
  /**
   * The document to open with. Read once, at mount — the editor owns the text from then on, and
   * re-creating it on every change would lose the cursor. Callers must therefore have the content
   * in hand before rendering this, and pass a `key` to open a different document.
   */
  value: string;
  onChange: (markdown: string) => void;
  pagePath: string;
  /**
   * Fires with *whether* anything is highlighted, not with the text.
   *
   * A boolean, because the page only needs it to decide a menu label, and serializing the selection
   * on every cursor move to answer that would be work per keystroke for a string nobody reads. The
   * text is pulled once, on demand, through {@link MilkdownHandle.selectedMarkdown}.
   */
  onSelectionChange?: (hasSelection: boolean) => void;
  handleRef?: RefObject<MilkdownHandle | null>;
}) {
  const { t } = useTranslation();
  const host = useRef<HTMLDivElement>(null);
  const crepe = useRef<CrepeInstance | null>(null);
  const prose = useRef<ProseApi | null>(null);
  const frontMatter = useRef<string | null>(null);
  const latest = useRef(onChange);
  const selectionListener = useRef(onSelectionChange);

  latest.current = onChange;
  selectionListener.current = onSelectionChange;

  useImperativeHandle(
    handleRef,
    (): MilkdownHandle => ({
      selectedMarkdown() {
        let markdown = '';

        withEditor((ctx, api) => {
          const view = ctx.get(api.editorViewCtx);
          const { from, to, empty } = view.state.selection;

          if (empty) {
            return;
          }

          // The serializer takes a node, not a slice, so the selected content is wrapped back into
          // a document of its own. Half a paragraph comes out as a paragraph — which is what the
          // user highlighted, and what they will get back.
          const fragment = view.state.schema.topNodeType.create(null, view.state.doc.slice(from, to).content);

          markdown = String(ctx.get(api.serializerCtx)(fragment)).trim();
        });

        return markdown;
      },

      replaceSelection(markdown: string) {
        withEditor((ctx, api) => {
          const view = ctx.get(api.editorViewCtx);
          const parsed = ctx.get(api.parserCtx)(markdown);

          if (!parsed) {
            return;
          }

          // `replaceSelection` with an open slice rather than `replaceWith` at the offsets: it lets
          // ProseMirror fit block content into an inline selection instead of throwing when a
          // rewritten sentence comes back as a paragraph of its own.
          view.dispatch(view.state.tr.replaceSelection(new api.Slice(parsed.content, 0, 0)).scrollIntoView());
          view.focus();
        });
      },
    }),
    [],
  );

  function withEditor(run: (ctx: Ctx, api: ProseApi) => void) {
    const instance = crepe.current;
    const api = prose.current;

    if (!instance || !api) {
      return;
    }

    try {
      instance.editor.action((ctx) => run(ctx, api));
    } catch {
      // A selection the schema cannot express, or an editor mid-teardown. Returning nothing leaves
      // the caller on the whole-page path, which always works — never a thrown error in the editor.
    }
  }

  useEffect(() => {
    let disposed = false;

    async function mount() {
      if (!host.current) {
        return;
      }

      const [{ Crepe }, core, model] = await Promise.all([
        import('@milkdown/crepe'),
        import('@milkdown/kit/core'),
        import('@milkdown/kit/prose/model'),
      ]);

      const split = splitFrontMatter(value);
      frontMatter.current = split.frontMatter;

      const instance = new Crepe({
        root: host.current,
        defaultValue: split.content,
        features: {
          // The toolbar exposes only what the canonical serializer round-trips. Anything on the
          // un-representable list is a feature we do not ship rather than one that damages a file.
          [Crepe.Feature.CodeMirror]: true,
          [Crepe.Feature.ListItem]: true,
          [Crepe.Feature.LinkTooltip]: true,
          [Crepe.Feature.ImageBlock]: true,
          [Crepe.Feature.BlockEdit]: true,
          [Crepe.Feature.Toolbar]: true,
          // The always-visible bar. The selection toolbar and the "/" menu only reward somebody who
          // already knows they exist; a person looking at an empty page sees nothing to click, and
          // "I could not find how to make a table" is the same outcome as not having tables. The
          // bar builds itself from the feature flags above, so Latex being off removes its button
          // rather than leaving one that writes something the serializer cannot round-trip.
          [Crepe.Feature.TopBar]: true,
          [Crepe.Feature.Table]: true,
          [Crepe.Feature.Placeholder]: true,
          [Crepe.Feature.Latex]: false,
        },
        featureConfigs: {
          [Crepe.Feature.Placeholder]: { text: t('editor.placeholder') },
          [Crepe.Feature.TopBar]: {
            // Translated, because Crepe's defaults are English literals and this is the only text
            // in the bar — everything else is an icon.
            //
            // All six levels are listed even though a page rarely goes past three: the style
            // dropdown reports the *current* block, and a level missing from this list is reported
            // as "Normal text". Pages written in VS Code do reach h4, and mislabelling one invites
            // the fix that flattens it.
            headingOptions: [
              { label: t('editor.toolbar.normalText'), level: null },
              ...[1, 2, 3, 4, 5, 6].map((level) => ({
                label: t('editor.toolbar.headingLevel', { level }),
                level,
              })),
            ],
          },
          [Crepe.Feature.ImageBlock]: {
            // An image pasted or dropped is uploaded and inserted. That is how real content
            // arrives, and it is the difference between "attach a photo of the serial number" and
            // "email it to yourself first".
            onUpload: async (file: File) => {
              if (!pagePath) {
                return '';
              }

              const uploaded = await api.uploadAttachment(pagePath, file);
              return `/api/v1/attachments/${encodePath(uploaded.path)}`;
            },
          },
        },
      });

      instance.on((listener) => {
        listener.markdownUpdated((_, markdown) => {
          const prefix = frontMatter.current ?? '';
          latest.current(prefix + markdown);
        });
      });

      await instance.create();

      if (disposed) {
        instance.destroy();
        return;
      }

      crepe.current = instance;
      prose.current = {
        editorViewCtx: core.editorViewCtx,
        serializerCtx: core.serializerCtx,
        parserCtx: core.parserCtx,
        Slice: model.Slice,
      };
    }

    void mount();

    return () => {
      disposed = true;
      crepe.current?.destroy();
      crepe.current = null;
      prose.current = null;
    };
    // Mount once: re-creating the editor on every keystroke would lose the cursor.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  /**
   * Whether something is highlighted, watched at the DOM rather than through a ProseMirror plugin.
   *
   * @remarks
   * <p>
   * A plugin would be the tidier answer inside one editor, but this has to behave the same way as
   * the Markdown textarea the source mode uses, and `selectionchange` is the one event both fire.
   * </p>
   * <p>
   * <strong>Only selections anchored inside this editor are answered for.</strong> Clicking the AI
   * button moves focus out of the contenteditable, and browsers differ on whether that collapses the
   * document selection — Chrome does. Reacting to it would clear the flag on the very click that
   * opens the menu, so every "Improve selection" would silently become "improve the whole page".
   * Ignoring selections that live elsewhere makes the flag sticky until the user next highlights
   * something in the editor, which is what they mean by it.
   * </p>
   * <p>
   * The flag going stale is safe in the direction that matters: the text itself comes from
   * ProseMirror's own state, which survives losing DOM focus, and a highlight that has genuinely
   * gone reads back as empty and falls the action back to the whole page.
   * </p>
   */
  useEffect(() => {
    if (!onSelectionChange) {
      return;
    }

    const handler = () => {
      const selection = document.getSelection();
      const anchor = selection?.anchorNode ?? null;

      if (anchor === null || !(host.current?.contains(anchor) ?? false)) {
        return;
      }

      selectionListener.current?.(!selection!.isCollapsed);
    };

    document.addEventListener('selectionchange', handler);

    return () => {
      document.removeEventListener('selectionchange', handler);
      // Leaving a stale "yes there is a selection" behind would offer to rewrite a range that no
      // longer exists once this editor is gone.
      selectionListener.current?.(false);
    };
  }, [onSelectionChange]);

  return (
    <Box
      ref={host}
      style={{
        minHeight: '60vh',
        border: '1px solid light-dark(var(--mantine-color-gray-3), var(--mantine-color-dark-4))',
        borderRadius: 'var(--mantine-radius-md)',
      }}
    />
  );
}
