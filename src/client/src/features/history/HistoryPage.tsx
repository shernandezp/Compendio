import { useState } from 'react';
import { useLocation } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Badge, Button, Card, Grid, Group, Loader, SegmentedControl, Stack, Text, Title } from '@mantine/core';
import { modals } from '@mantine/modals';
import { notifications } from '@mantine/notifications';

import { api } from '../../lib/api';
import { formatDate, formatRelative } from '../../lib/format';

/**
 * History, with two diff views.
 *
 * The source diff is for the IT admin. The rendered diff — block-level added/removed/changed over
 * the rendered HTML — is what makes this feature usable by the HR manager, who is the person most
 * likely to be asking what changed in a policy.
 */
export function HistoryPage() {
  const { t, i18n } = useTranslation();
  const location = useLocation();
  const queryClient = useQueryClient();

  const path = decodeURIComponent(location.pathname.replace(/^\/history\//, ''));
  const [view, setView] = useState<'rendered' | 'source'>('rendered');
  const [selection, setSelection] = useState<{ from?: string; to?: string }>({});

  const versions = useQuery({ queryKey: ['versions', path], queryFn: () => api.versions(path) });

  const from = selection.from ?? versions.data?.[1]?.id;
  const to = selection.to ?? versions.data?.[0]?.id;

  const diff = useQuery({
    queryKey: ['diff', path, from, to],
    queryFn: () => api.diff(path, from!, to!),
    enabled: Boolean(from && to && from !== to),
  });

  const restore = useMutation({
    mutationFn: (id: string) => api.restore(id, path),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['versions', path] });
      await queryClient.invalidateQueries({ queryKey: ['page', path] });
      notifications.show({ message: t('history.restored') });
    },
  });

  if (versions.isPending) {
    return <Loader />;
  }

  return (
    <Grid>
      <Grid.Col span={{ base: 12, md: 4 }}>
        <Stack gap="xs">
          <Title order={3}>{t('history.versions')}</Title>

          {(versions.data ?? []).map((version) => (
            <Card
              key={version.id}
              withBorder
              padding="sm"
              style={{ cursor: 'pointer', borderColor: version.id === to ? 'var(--mantine-color-indigo-5)' : undefined }}
              onClick={() => setSelection((s) => ({ from: s.to ?? from, to: version.id }))}
            >
              <Group justify="space-between" wrap="nowrap">
                <div>
                  <Text size="sm" fw={600}>
                    v{version.sequence}
                  </Text>
                  <Text size="xs" c="dimmed">
                    {formatDate(version.createdAt, i18n.language)}
                  </Text>
                  <Text size="xs" c="dimmed">
                    {/* Attribution honesty: an external edit is recorded as one and credited to
                        nobody, rather than to whoever happened to be signed in. */}
                    {version.authorDisplayName ?? t('history.noAuthor')}
                  </Text>
                </div>
                <Badge size="xs" variant="light">
                  {t(`history.source.${version.source}`)}
                </Badge>
              </Group>

              <Button
                size="compact-xs"
                variant="subtle"
                mt="xs"
                onClick={(event) => {
                  event.stopPropagation();
                  modals.openConfirmModal({
                    title: t('history.restore'),
                    children: (
                      <Text size="sm">
                        {t('history.restoreConfirm', {
                          when: formatRelative(version.createdAt, i18n.language),
                        })}
                      </Text>
                    ),
                    labels: { confirm: t('history.restore'), cancel: t('app.cancel') },
                    onConfirm: () => restore.mutate(version.id),
                  });
                }}
              >
                {t('history.restore')}
              </Button>
            </Card>
          ))}
        </Stack>
      </Grid.Col>

      <Grid.Col span={{ base: 12, md: 8 }}>
        <Stack gap="md">
          <Group justify="space-between">
            <Title order={3}>{t('history.compare')}</Title>
            <SegmentedControl
              size="xs"
              value={view}
              onChange={(value) => setView(value as 'rendered' | 'source')}
              data={[
                { value: 'rendered', label: t('history.renderedDiff') },
                { value: 'source', label: t('history.sourceDiff') },
              ]}
            />
          </Group>

          {diff.isPending && from && to ? (
            <Loader size="sm" />
          ) : diff.data ? (
            <>
              <Group gap="xs">
                <Badge color="green" variant="light">
                  +{diff.data.addedLines} {t('history.added')}
                </Badge>
                <Badge color="red" variant="light">
                  −{diff.data.removedLines} {t('history.removed')}
                </Badge>
              </Group>

              {view === 'rendered' ? (
                <div
                  className="compendio-content"
                  // Sanitized server-side before it ever leaves the API.
                  dangerouslySetInnerHTML={{ __html: diff.data.renderedHtml }}
                />
              ) : (
                <div className="compendio-diff">
                  {diff.data.source.map((line, index) => (
                    <span key={index} className={`line ${line.kind}`}>
                      {line.text}
                    </span>
                  ))}
                </div>
              )}
            </>
          ) : (
            <Text c="dimmed">{t('history.compare')}</Text>
          )}
        </Stack>
      </Grid.Col>
    </Grid>
  );
}
