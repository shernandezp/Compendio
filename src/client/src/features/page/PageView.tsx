import { useEffect, useRef, useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Anchor,
  Badge,
  Button,
  Card,
  Divider,
  Grid,
  Group,
  Loader,
  Stack,
  Text,
  Title,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconEdit, IconHistory, IconInfoCircle, IconLock } from '@tabler/icons-react';

import { AiMenu } from '../ai/AiMenu';
import { AcknowledgmentBanner, MachineTranslationBanner, StaleBanner } from '../lifecycle/PageBanners';
import { LifecyclePanel } from '../lifecycle/LifecyclePanel';
import { AcknowledgmentReport } from '../acknowledgments/AcknowledgmentReport';

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

          {data.attachments.length > 0 && (
            <Card withBorder padding="sm">
              <Text size="xs" fw={700} tt="uppercase" c="dimmed" mb="xs">
                {t('page.attachments')}
              </Text>
              <Stack gap={4}>
                {data.attachments.map((attachment) => (
                  <Anchor key={attachment.path} href={`/api/v1/attachments/${attachment.path}`} size="sm">
                    {attachment.name}
                  </Anchor>
                ))}
              </Stack>
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
