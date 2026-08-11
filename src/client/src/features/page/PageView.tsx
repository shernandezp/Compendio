import { useEffect, useRef, useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Button,
  Card,
  Divider,
  FileButton,
  Grid,
  Group,
  Loader,
  Stack,
  Text,
  Title,
} from '@mantine/core';
import { modals } from '@mantine/modals';
import { notifications } from '@mantine/notifications';
import { IconEdit, IconHistory, IconInfoCircle, IconLock, IconPaperclip, IconTrash } from '@tabler/icons-react';

import { AiMenu } from '../ai/AiMenu';
import { AcknowledgmentBanner, MachineTranslationBanner, StaleBanner } from '../lifecycle/PageBanners';
import { LifecyclePanel } from '../lifecycle/LifecyclePanel';
import { AcknowledgmentReport } from '../acknowledgments/AcknowledgmentReport';
import { ImageLightbox } from './ImageLightbox';
import { attachmentUrl } from './attachmentRefs';

import { api, ApiError, encodePath } from '../../lib/api';
import { formatRelative } from '../../lib/format';
import { renderDiagrams } from '../../lib/mermaid';

export function PageView() {
  const { t, i18n } = useTranslation();
  const location = useLocation();

  const queryClient = useQueryClient();
  const [lifecycleOpen, setLifecycleOpen] = useState(false);
  const [reportOpen, setReportOpen] = useState(false);

  const path = decodeURIComponent(location.pathname.replace(/^\/p\//, ''));
  const contentRef = useRef<HTMLDivElement>(null);

  /**
   * Clears the file input after each pick.
   *
   * Without it the input keeps the last file it was given, and choosing the same file again fires
   * no change event at all — so a failed upload could not simply be retried.
   */
  const resetFilePicker = useRef<() => void>(null);

  const page = useQuery({
    queryKey: ['page', path],
    queryFn: () => api.page(path),
    retry: false,
  });

  const backlinks = useQuery({
    queryKey: ['backlinks', path],
    queryFn: () => api.backlinks(path),
    enabled: page.isSuccess,
  });

  // Diagrams render after the HTML lands, with securityLevel 'strict'.
  useEffect(() => {
    if (page.data?.containsMermaid && contentRef.current) {
      void renderDiagrams(contentRef.current);
    }
  }, [page.data?.containsMermaid, page.data?.html]);

  /**
   * **Add a file**, which the guide has always described and nothing rendered.
   *
   * No `accept` filter on the picker: the allowed types are an administrator's setting on the
   * server, and a list hard-coded here would drift from it and silently hide types an instance
   * allows. The server answers a rejection in the reader's own language, so the honest thing is to
   * let it decide and to show what it said.
   */
  const uploadAttachment = useMutation({
    mutationFn: (file: File) => api.uploadAttachment(path, file),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['page', path] });
      notifications.show({ message: t('page.attachmentAdded') });
    },
    onError: (error) =>
      notifications.show({
        color: 'red',
        message: error instanceof ApiError && error.detail ? error.detail : t('page.attachmentFailed'),
      }),
  });

  /**
   * Deleting an attachment.
   *
   * @remarks
   * One call. The server removes the images that pointed at the file from the page and then deletes
   * the file, in that order and against the hash it just read — so a page edited in the meantime
   * comes back as a conflict with nothing deleted, rather than as a hole in somebody's paragraph.
   * Doing it in two calls from here could not promise that.
   */
  const deleteAttachment = useMutation({
    mutationFn: (attachment: { path: string; name: string }) => api.deleteAttachment(attachment.path),
    onSuccess: () => notifications.show({ message: t('page.attachmentDeleted') }),
    onError: (error) =>
      notifications.show({
        color: 'red',
        message:
          error instanceof ApiError && error.isConflict
            ? t('page.attachmentDeleteConflict')
            : error instanceof ApiError && error.detail
              ? error.detail
              : t('page.attachmentDeleteFailed'),
      }),
    // Whichever way it went, the page on screen is no longer what the server holds: a success
    // removed a picture from it, and a failure may have removed one before the delete itself broke.
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['page', path] }),
  });

  /** The confirmation, shared by the two places an attachment can be deleted from. */
  const confirmDelete = (attachment: { path: string; name: string }) =>
    modals.openConfirmModal({
      title: t('page.deleteAttachment'),
      children: <Text size="sm">{t('page.deleteAttachmentConfirm', { name: attachment.name })}</Text>,
      labels: { confirm: t('page.deleteAttachment'), cancel: t('common.cancel') },
      confirmProps: { color: 'red' },
      onConfirm: () => deleteAttachment.mutate(attachment),
    });

  const toggleCheckbox = useMutation({
    mutationFn: (input: { offset: number; checked: boolean }) =>
      api.toggleCheckbox(path, input.offset, input.checked, page.data!.contentHash),
    onSuccess: (updated) => {
      queryClient.setQueryData(['page', path], updated);
    },
    onError: () => notifications.show({ color: 'red', message: t('app.error.generic') }),
  });

  // Ticking a checklist item from read mode. The one interaction the mobile scenario is built
  // around, and the reason the server has a byte-substitution endpoint at all.
  useEffect(() => {
    const container = contentRef.current;
    if (!container || !page.data?.content) {
      return;
    }

    const boxes = Array.from(container.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'));
    const offsets = findCheckboxOffsets(page.data.content);
    const canWrite = page.data.level === 'Write' || page.data.level === 'Manage';
    const bound: { box: HTMLInputElement; handler: (event: Event) => void }[] = [];

    boxes.forEach((box, index) => {
      box.disabled = !canWrite;

      const offset = offsets[index];
      if (!canWrite || offset === undefined) {
        return;
      }

      const handler = () => {
        // `box.checked` is already the state the click moved it to; the server validates the
        // offset against the expected old text and the content hash, so a stale offset comes back
        // as a conflict rather than as a wrong edit.
        toggleCheckbox.mutate({ offset, checked: box.checked });
      };

      box.addEventListener('change', handler);
      bound.push({ box, handler });
    });

    return () => {
      for (const { box, handler } of bound) {
        box.removeEventListener('change', handler);
      }
    };
    // toggleCheckbox is a stable mutation object; adding it would rebind on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [page.data?.html, page.data?.content, page.data?.level]);

  if (page.isPending) {
    return <Loader />;
  }

  if (page.isError) {
    const notFound = page.error instanceof ApiError && page.error.isNotFound;
    return (
      <Alert color={notFound ? 'gray' : 'red'} title={t('app.error.title')}>
        {notFound ? t('app.error.notFound') : t('app.error.generic')}
      </Alert>
    );
  }

  const data = page.data;
  const canWrite = data.level === 'Write' || data.level === 'Manage';

  return (
    <Grid>
      <Grid.Col span={{ base: 12, lg: 8 }}>
        <Stack gap="md">
          <Group justify="space-between" align="flex-start" wrap="nowrap">
            <div>
              <Title order={1}>{data.title}</Title>
              <Group gap="xs" mt={4}>
                <Text size="sm" c="dimmed">
                  {data.lastEditWasExternal
                    ? t('page.updatedExternally', { when: formatRelative(data.updatedAt, i18n.language) })
                    : data.updatedBy
                      ? t('page.updatedBy', {
                          when: formatRelative(data.updatedAt, i18n.language),
                          who: data.updatedBy,
                        })
                      : t('page.updated', { when: formatRelative(data.updatedAt, i18n.language) })}
                </Text>
                {data.isSecure && (
                  <Badge color="orange" leftSection={<IconLock size={12} />} variant="light">
                    {t('page.secure')}
                  </Badge>
                )}
                {data.tags.map((tag) => (
                  <Badge key={tag} variant="light" size="sm">
                    {tag}
                  </Badge>
                ))}
              </Group>
            </div>

            <Group gap="xs" wrap="nowrap">
              {/* Renders nothing at all when no AI provider is configured. No onAccept here: the
                  read view has nowhere to put a rewrite, so it offers none. */}
              <AiMenu path={data.path} />

              {canWrite && (
                <Button variant="subtle" onClick={() => setLifecycleOpen(true)}>
                  {t('lifecycle.panelTitle')}
                </Button>
              )}

              {data.requiresAcknowledgment && data.level === 'Manage' && (
                <Button variant="subtle" onClick={() => setReportOpen(true)}>
                  {t('acknowledgment.report')}
                </Button>
              )}

              <Button
                component={Link}
                to={`/history/${encodePath(data.path)}`}
                leftSection={<IconHistory size={16} />}
                variant="subtle"
              >
                {t('page.history')}
              </Button>
              {canWrite && (
                <Button
                  component={Link}
                  to={`/edit/${encodePath(data.path)}`}
                  leftSection={<IconEdit size={16} />}
                  variant="filled"
                >
                  {t('page.edit')}
                </Button>
              )}
            </Group>
          </Group>

          {/* The three lifecycle surfaces meet here: a page past its review date is impossible to
              miss, a policy owing acknowledgment asks for it, and a machine translation says so. */}
          <StaleBanner page={data} />
          {data.requiresAcknowledgment && <AcknowledgmentBanner path={data.path} />}
          <MachineTranslationBanner content={data.content} />

          {data.isSecure && (
            <Alert variant="light" color="orange" icon={<IconLock size={18} />}>
              {t('page.secureHint')}
            </Alert>
          )}

          {!data.isCanonical && canWrite && (
            <Alert variant="light" icon={<IconInfoCircle size={18} />}>
              {t('page.notCanonical')}
            </Alert>
          )}

          {data.translations.some((tr) => tr.isStale) && (
            <Alert variant="light" color="yellow">
              {t('page.translationStale')}
            </Alert>
          )}

          <Divider />

          {data.html && data.html.trim().length > 0 ? (
            <div
              ref={contentRef}
              className="compendio-content"
              // Sanitized server-side by Ganss.Xss before it ever reaches the browser; the CSP is
              // defence in depth on top of that, not instead of it.
              dangerouslySetInnerHTML={{ __html: data.html }}
            />
          ) : (
            <Text c="dimmed">{t('page.empty')}</Text>
          )}

          {/* Binds itself to the images inside the markup above; renders nothing until one is
              clicked. */}
          <ImageLightbox
            containerRef={contentRef}
            html={data.html}
            attachments={data.attachments}
            onDelete={canWrite ? confirmDelete : undefined}
          />
        </Stack>
      </Grid.Col>

      <Grid.Col span={{ base: 12, lg: 4 }}>
        <Stack gap="md">
          {data.headings.length > 1 && (
            <Card withBorder padding="sm">
              <Text size="xs" fw={700} tt="uppercase" c="dimmed" mb="xs">
                {t('page.tableOfContents')}
              </Text>
              <Stack gap={2}>
                {data.headings.map((heading) => (
                  <Anchor
                    key={heading.anchor}
                    href={`#${heading.anchor}`}
                    size="sm"
                    style={{ paddingInlineStart: (heading.level - 1) * 12 }}
                  >
                    {heading.text}
                  </Anchor>
                ))}
              </Stack>
            </Card>
          )}

          {data.translations.length > 0 && (
            <Card withBorder padding="sm">
              <Text size="xs" fw={700} tt="uppercase" c="dimmed" mb="xs">
                {t('page.translations')}
              </Text>
              <Stack gap={4}>
                {data.translations.map((tr) => (
                  <Group key={tr.path} gap="xs">
                    <Badge size="xs" variant="light">
                      {tr.lang}
                    </Badge>
                    <Anchor component={Link} to={`/p/${encodePath(tr.path)}`} size="sm">
                      {tr.title}
                    </Anchor>
                  </Group>
                ))}
              </Stack>
            </Card>
          )}

          <Card withBorder padding="sm">
            <Text size="xs" fw={700} tt="uppercase" c="dimmed" mb="xs">
              {t('page.backlinks')}
            </Text>
            {backlinks.data && backlinks.data.length > 0 ? (
              <Stack gap={4}>
                {backlinks.data.map((hit) => (
                  <Anchor key={hit.path} component={Link} to={`/p/${encodePath(hit.path)}`} size="sm">
                    {hit.title}
                  </Anchor>
                ))}
              </Stack>
            ) : (
              <Text size="sm" c="dimmed">
                {t('page.noBacklinks')}
              </Text>
            )}
          </Card>

          {/* Shown to a writer even when empty: the first attachment has to be addable from
              somewhere, and a card that appears only once something is in it never can be. */}
          {(data.attachments.length > 0 || canWrite) && (
            <Card withBorder padding="sm">
              <Text size="xs" fw={700} tt="uppercase" c="dimmed" mb="xs">
                {t('page.attachments')}
              </Text>
              <Stack gap={4}>
                {data.attachments.map((attachment) => (
                  <Group key={attachment.path} gap="xs" wrap="nowrap" justify="space-between">
                    {/* Encoded segment by segment: a folder called "Router #2" would otherwise
                        truncate the request at the '#' and ask for a file that is not there. */}
                    <Anchor href={attachmentUrl(attachment.path)} size="sm" style={{ overflowWrap: 'anywhere' }}>
                      {attachment.name}
                    </Anchor>

                    {/* Also here, not only in the image viewer: a PDF has no preview to click. */}
                    {canWrite && (
                      <ActionIcon
                        variant="subtle"
                        color="red"
                        size="sm"
                        // The delete edits the page and reindexes it, so it is not always instant.
                        loading={deleteAttachment.isPending && deleteAttachment.variables?.path === attachment.path}
                        aria-label={t('page.deleteAttachmentNamed', { name: attachment.name })}
                        onClick={() => confirmDelete(attachment)}
                      >
                        <IconTrash size={14} />
                      </ActionIcon>
                    )}
                  </Group>
                ))}
              </Stack>

              {canWrite && (
                <FileButton
                  resetRef={resetFilePicker}
                  onChange={(file) => {
                    if (file) {
                      uploadAttachment.mutate(file);
                    }

                    resetFilePicker.current?.();
                  }}
                >
                  {(props) => (
                    <Button
                      {...props}
                      variant="subtle"
                      size="xs"
                      mt={data.attachments.length > 0 ? 'xs' : 0}
                      loading={uploadAttachment.isPending}
                      leftSection={<IconPaperclip size={14} />}
                    >
                      {t('page.addAttachment')}
                    </Button>
                  )}
                </FileButton>
              )}
            </Card>
          )}
        </Stack>
      </Grid.Col>

      <LifecyclePanel page={data} opened={lifecycleOpen} onClose={() => setLifecycleOpen(false)} />
      <AcknowledgmentReport path={data.path} opened={reportOpen} onClose={() => setReportOpen(false)} />
    </Grid>
  );
}

/**
 * Byte offsets of every `[ ]` / `[x]` marker in the source, in document order.
 *
 * These are matched positionally against the checkboxes the renderer produced, so the two have to
 * count the same things. Fenced code is skipped for exactly that reason: a runbook that documents
 * Markdown contains `- [ ]` inside a fence, the renderer leaves it as code, and counting it here
 * would shift every real checkbox after it by one — the server would then happily tick a different
 * line, because the text at that offset really is `[ ]`.
 *
 * The server still validates the offset against the expected old text and the content hash, so a
 * stale offset is a conflict rather than a wrong edit.
 */
function findCheckboxOffsets(content: string): number[] {
  const encoder = new TextEncoder();
  const offsets: number[] = [];
  const pattern = /^(\s*[-*+]\s+)(\[[ xX]\])/;

  let fence: string | null = null;
  let characterOffset = 0;

  for (const line of content.split('\n')) {
    const trimmed = line.trimStart();

    if (fence !== null) {
      if (trimmed.startsWith(fence)) {
        fence = null;
      }
    } else if (trimmed.startsWith('```') || trimmed.startsWith('~~~')) {
      fence = trimmed.slice(0, 3);
    } else {
      const match = pattern.exec(line);
      if (match) {
        const markerAt = characterOffset + (match[1]?.length ?? 0);
        offsets.push(encoder.encode(content.slice(0, markerAt)).length);
      }
    }

    characterOffset += line.length + 1;
  }

  return offsets;
}
