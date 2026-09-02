import { useCallback, useEffect, useRef, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Checkbox,
  Group,
  Loader,
  Modal,
  SegmentedControl,
  Select,
  Stack,
  Text,
  Textarea,
  TextInput,
  Title,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { IconAlertTriangle, IconDeviceFloppy } from '@tabler/icons-react';

import { api, ApiError, encodePath, type Page } from '../../lib/api';
import { canonicalize } from '../../lib/markdown/canonical';
import { AiMenu } from '../ai/AiMenu';
import { AiDraftPanel } from '../ai/AiDraftPanel';
import { MilkdownEditor, type MilkdownHandle } from './MilkdownEditor';
import { ConflictResolver } from './ConflictResolver';
import { clearDraft, loadDraft, saveDraft } from './drafts';

/**
 * The editor.
 *
 * Rich text is the default and only visible mode — nobody sees `##`, `**` or `|---|` unless they
 * ask. A Markdown source toggle exists for people who prefer it, and Markdown input shortcuts keep
 * working while typing either way.
 *
 * Two promises this screen has to keep: never lose work, and never resolve a conflict by guessing.
 */
export function EditorPage() {
  const { t } = useTranslation();
  const location = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const isNarrow = useMediaQuery('(max-width: 48em)');

  const rawPath = decodeURIComponent(location.pathname.replace(/^\/edit\//, ''));
  const isNew = rawPath === 'new' || rawPath === '';

  // Which folder a new page goes in. The tree passes it when "new page" is used from a folder;
  // without it a page created from the header would always land at the root.
  const folderPath = new URLSearchParams(location.search).get('folder') ?? '';

  const [mode, setMode] = useState<'rich' | 'source'>('rich');
  const [title, setTitle] = useState('');
  const [content, setContent] = useState('');
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState(false);
  const [conflict, setConflict] = useState<{ mine: string; theirs: string; hash: string } | null>(null);
  const [recoveredAt, setRecoveredAt] = useState<string | null>(null);

  // The editor's explicit answer to "does everyone need to read this again?". Default off, so an
  // ordinary edit never re-opens an acknowledgment — re-asking two hundred people to re-read a typo
  // fix is how the feature gets switched off.
  const [material, setMaterial] = useState(false);

  /**
   * The editor is uncontrolled — it takes its document once, at mount, and owns it from then on.
   * So it must not mount before `content` holds the page: state set in an effect lands *after* the
   * child's mount effect has already read the value, and the editor would open empty on a page that
   * is not. The first keystroke would then be a save that empties the file.
   */
  const [loadedPath, setLoadedPath] = useState<string | null>(null);
  const ready = isNew || loadedPath === rawPath;

  const baselineHash = useRef<string>('');

  /**
   * Bumped whenever the buffer is replaced from outside the editor rather than typed into it.
   *
   * It is part of the editor's `key`, so a wholesale replacement remounts it on the new text. The
   * editor is uncontrolled by design — that is what keeps the cursor alive between keystrokes — so
   * without this, accepting an AI proposal or discarding a recovered draft would change `content`
   * while the editor kept showing the old document, and the next save would write text the person
   * looking at the screen had never seen.
   */
  const [revision, setRevision] = useState(0);

  const editor = useRef<MilkdownHandle | null>(null);
  const source = useRef<HTMLTextAreaElement>(null);

  /**
   * Whether anything is highlighted right now.
   *
   * Tracked as a boolean and turned into text only when an AI action is actually invoked: the label
   * needs to know *that* there is a selection, and serializing one on every cursor move to answer
   * that would be work per keystroke for a string nobody reads.
   */
  const [hasSelection, setHasSelection] = useState(false);

  /** Replaces the whole buffer, and makes the editor show it. */
  const replaceContent = useCallback((next: string) => {
    setContent(next);
    setDirty(true);
    setRevision((n) => n + 1);
  }, []);

  /** The highlighted Markdown, from whichever of the two editors is on screen. */
  const readSelection = useCallback(() => {
    if (mode === 'source') {
      const textarea = source.current;

      if (!textarea || textarea.selectionStart === textarea.selectionEnd) {
        return '';
      }

      return textarea.value.slice(textarea.selectionStart, textarea.selectionEnd).trim();
    }

    return editor.current?.selectedMarkdown() ?? '';
  }, [mode]);

  /**
   * Puts a proposal back over the range it was made from, leaving the rest of the page alone.
   *
   * @remarks
   * Two editors, two mechanisms, one rule: replace exactly what was sent. In rich text ProseMirror
   * splices the parsed Markdown into the current selection, which keeps the cursor and the undo
   * history intact — so a user who dislikes the rewrite gets it back with Ctrl+Z like any other
   * edit. In source mode the offsets are exact, so the splice is a string operation and the caret
   * lands after the inserted text rather than back at the top.
   *
   * A selection that has since disappeared falls back to appending nothing and leaving the buffer
   * untouched: silently rewriting the whole page instead would be the worst available outcome.
   */
  const applyToSelection = useCallback((markdown: string) => {
    if (mode === 'rich') {
      editor.current?.replaceSelection(markdown);
      setDirty(true);
      return;
    }

    const textarea = source.current;

    if (!textarea || textarea.selectionStart === textarea.selectionEnd) {
      return;
    }

    const { selectionStart, selectionEnd, value } = textarea;
    const next = value.slice(0, selectionStart) + markdown + value.slice(selectionEnd);

    setContent(next);
    setDirty(true);

    requestAnimationFrame(() => {
      textarea.focus();
      textarea.setSelectionRange(selectionStart, selectionStart + markdown.length);
    });
  }, [mode]);

  const page = useQuery({
    queryKey: ['page', rawPath, 'raw'],
    queryFn: () => api.page(rawPath, true),
    enabled: !isNew,
    retry: false,
  });

  /**
   * The template picker, on a new page only.
   *
   * The guide has always offered one — Procedure, Runbook, Policy, Meeting notes — and the catalogue
   * is what the AI draft already reads, so a page started from a template and a draft asked to
   * follow one share the same shape. Choosing one replaces the buffer, through the revision counter,
   * so the editor shows it; it is offered while the page is still empty, because replacing a
   * paragraph somebody typed with a skeleton is not what "template" means.
   */
  const templates = useQuery({
    queryKey: ['templates'],
    queryFn: api.templates,
    staleTime: 5 * 60 * 1000,
    enabled: isNew,
  });
  const [templateId, setTemplateId] = useState<string | null>(null);

  function applyTemplate(id: string | null) {
    setTemplateId(id);
    const template = (templates.data ?? []).find((candidate) => candidate.id === id);
    replaceContent(template?.content ?? '');
    // A skeleton is not something to warn about losing.
    setDirty(false);
  }

  useEffect(() => {
    if (!page.data) {
      return;
    }

    baselineHash.current = page.data.contentHash;

    // A local draft beats the server copy, because a draft only exists when a previous session was
    // interrupted before saving.
    const draft = loadDraft(rawPath);
    if (draft && draft.baselineHash === page.data.contentHash && draft.content !== page.data.content) {
      setContent(draft.content);
      setRecoveredAt(draft.savedAt);
      setDirty(true);
    } else {
      setContent(page.data.content ?? '');
    }

    setTitle(page.data.title);
    setLoadedPath(rawPath);
  }, [page.data, rawPath]);

  // Continuous local draft. Cheap, synchronous, and the reason a closed tab costs nothing.
  useEffect(() => {
    if (!dirty || isNew) {
      return;
    }

    const handle = setTimeout(() => saveDraft(rawPath, content, baselineHash.current), 800);
    return () => clearTimeout(handle);
  }, [content, dirty, isNew, rawPath]);

  // The unsaved-changes guard.
  useEffect(() => {
    if (!dirty) {
      return;
    }

    const handler = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };

    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [dirty]);

  const onChange = useCallback((next: string) => {
    setContent(next);
    setDirty(true);
  }, []);

  /**
   * `text` is passed explicitly rather than read from state, because the conflict resolver calls
   * this immediately after `setContent` — and React state updates are not synchronous, so reading
   * `content` here would save the *pre-merge* text and quietly discard the merge.
   */
  async function save(text: string = content, baseline: string = baselineHash.current) {
    setBusy(true);

    try {
      // remark in the client is the only writer of Markdown in the product. The one-time
      // normalization happens here, on the first human save of a page written elsewhere.
      const canonical = canonicalize(text);
      const normalized = !isNew && page.data !== undefined && !page.data.isCanonical;

      let saved: Page;
      if (isNew) {
        saved = await api.createPage({ folderPath, title, content: canonical });
      } else {
        saved = await api.savePage(rawPath, canonical, baseline, normalized, material);
      }

      clearDraft(rawPath);
      setDirty(false);
      baselineHash.current = saved.contentHash;

      await queryClient.invalidateQueries({ queryKey: ['tree'] });
      await queryClient.invalidateQueries({ queryKey: ['page', saved.path] });

      notifications.show({ message: isNew ? t('page.created') : t('page.saved') });
      navigate(`/p/${encodePath(saved.path)}`);
    } catch (error) {
      if (error instanceof ApiError && error.isConflict) {
        // A conflict gets real UI, not an alert box. This is the moment somebody could lose an
        // hour's work.
        setConflict({
          mine: text,
          theirs: (error.extensions.currentContent as string) ?? '',
          hash: (error.extensions.actualHash as string) ?? '',
        });
      } else if (error instanceof ApiError && error.status === 403) {
        // A permission denial is not a transient failure: "try again" would send the user round a
        // loop that can never succeed. The server's detail already explains it — you can read this
        // folder but not write to it, or it is encrypted and admin-only — and says what to do next.
        notifications.show({ color: 'red', message: error.detail || t('editor.saveForbidden') });
      } else {
        // The buffer survives a failed save, deliberately.
        notifications.show({ color: 'red', message: t('editor.saveFailed') });
      }
    } finally {
      setBusy(false);
    }
  }

  if (!isNew && page.isError) {
    return (
      <Alert color="red" icon={<IconAlertTriangle size={18} />}>
        {page.error instanceof ApiError && page.error.isNotFound
          ? t('app.error.notFound')
          : t('app.error.generic')}
      </Alert>
    );
  }

  if (!isNew && (page.isPending || !ready)) {
    return <Loader />;
  }

  return (
    <Stack gap="md">
      <Group justify="space-between" wrap="nowrap">
        <Title order={2}>{isNew ? t('nav.newPage') : title}</Title>

        <Group gap="xs" wrap="nowrap">
          {/* A page that does not exist yet has nothing to improve, summarize or translate — but it
              is exactly where turning rough notes into a first draft belongs. */}
          {isNew ? (
            <AiDraftPanel folderPath={folderPath} onDraft={replaceContent} />
          ) : (
            <AiMenu
              path={rawPath}
              selection={{ active: hasSelection, read: readSelection }}
              onAccept={(markdown, scope) => {
                if (scope === 'selection') {
                  applyToSelection(markdown);
                  return;
                }

                replaceContent(markdown);
              }}
            />
          )}

          <SegmentedControl
            size="xs"
            value={mode}
            onChange={(value) => {
              // The highlight does not survive the swap, and a stale flag would offer to rewrite a
              // range the new editor knows nothing about.
              setHasSelection(false);
              setMode(value as 'rich' | 'source');
            }}
            data={[
              { value: 'rich', label: t('editor.richText') },
              { value: 'source', label: t('editor.markdownSource') },
            ]}
          />
          {/* Only where it can mean anything: a page nobody has to acknowledge has nothing to
              re-open, and an unconditional checkbox would be a question without a subject. */}
          {page.data?.requiresAcknowledgment && (
            <Checkbox
              size="xs"
              checked={material}
              onChange={(event) => setMaterial(event.currentTarget.checked)}
              label={t('acknowledgment.materialRevision')}
              description={t('acknowledgment.materialRevisionHint')}
            />
          )}

          <Button
            onClick={() => void save()}
            loading={busy}
            leftSection={<IconDeviceFloppy size={16} />}
            disabled={isNew && title.trim().length === 0}
          >
            {t('app.save')}
          </Button>
        </Group>
      </Group>

      {recoveredAt && (
        <Alert
          color="yellow"
          icon={<IconAlertTriangle size={18} />}
          withCloseButton
          onClose={() => setRecoveredAt(null)}
        >
          <Group justify="space-between">
            <Text size="sm">{t('editor.draftRecovered', { when: new Date(recoveredAt).toLocaleString() })}</Text>
            <Button
              size="xs"
              variant="subtle"
              onClick={() => {
                clearDraft(rawPath);
                // Through the revision counter, or the editor would keep showing the recovered
                // draft it was just told to discard.
                replaceContent(page.data?.content ?? '');
                setRecoveredAt(null);
                setDirty(false);
              }}
            >
              {t('editor.discardDraft')}
            </Button>
          </Group>
        </Alert>
      )}

      {isNew && (
        <Group align="flex-end" grow>
          <TextInput
            label={t('page.titleLabel')}
            placeholder={t('page.newTitle')}
            value={title}
            onChange={(event) => setTitle(event.currentTarget.value)}
            required
            autoFocus
          />
          <Select
            label={t('page.template')}
            description={t('page.templateHint')}
            placeholder={t('template.blank')}
            value={templateId}
            onChange={applyTemplate}
            // Once something has been typed the choice is closed: a template is a starting shape,
            // not a way to lose a paragraph.
            disabled={dirty && content.trim().length > 0}
            clearable
            data={(templates.data ?? [])
              .filter((template) => template.id !== 'blank')
              .map((template) => ({
                value: template.id,
                // Bundled titles are i18n keys; an organization's own are literal text.
                label: t(template.title, { defaultValue: template.title }),
              }))}
          />
        </Group>
      )}

      {mode === 'rich' ? (
        // Keyed on the path so navigating between pages remounts the editor with the new document,
        // rather than leaving the previous one on screen.
        <MilkdownEditor
          key={`${rawPath}:${revision}`}
          value={content}
          onChange={onChange}
          pagePath={isNew ? '' : rawPath}
          onSelectionChange={setHasSelection}
          handleRef={editor}
        />
      ) : (
        <Textarea
          ref={source}
          value={content}
          onChange={(event) => onChange(event.currentTarget.value)}
          // Deliberately not cleared on blur: clicking the AI button blurs the textarea, and
          // forgetting the highlight at that exact moment would turn "improve this paragraph" into
          // "improve this page" for everybody who reaches the menu with a mouse.
          onSelect={(event) => {
            const textarea = event.currentTarget;
            setHasSelection(textarea.selectionStart !== textarea.selectionEnd);
          }}
          autosize
          minRows={20}
          styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)', fontSize: 13 } }}
          aria-label={t('editor.markdownSource')}
        />
      )}

      <Modal
        opened={conflict !== null}
        onClose={() => setConflict(null)}
        title={t('editor.conflict.title')}
        size="90rem"
        fullScreen={isNarrow}
      >
        {conflict &&
          (isNarrow ? (
            // A three-pane merge does not fit 360 px, and pretending it does loses somebody's work.
            <Alert color="yellow">{t('editor.conflict.resolveOnDesktop')}</Alert>
          ) : (
            <ConflictResolver
              mine={conflict.mine}
              theirs={conflict.theirs}
              onResolve={async (merged) => {
                // Both the merged text and the hash it was merged against are passed explicitly.
                // Relying on the state updates having landed is how a merge gets thrown away.
                baselineHash.current = conflict.hash;
                setContent(merged);
                setConflict(null);
                await save(merged, conflict.hash);
              }}
              onCancel={() => setConflict(null)}
            />
          ))}
      </Modal>
    </Stack>
  );
}
