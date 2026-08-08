import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Anchor, Badge, Button, Card, Group, Loader, Stack, Text, Title } from '@mantine/core';

import { api, encodePath, type Notification } from '../../lib/api';
import { formatRelative } from '../../lib/format';

/**
 * The per-user inbox.
 *
 * Every row here has already been re-checked against the permission evaluator by the server: a
 * notification whose page the recipient can no longer read is dropped from the response and deleted.
 * The screen therefore renders what it was given and does no filtering of its own.
 */
export function NotificationsPage() {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();

  const notifications = useQuery({
    queryKey: ['notifications'],
    queryFn: () => api.notifications(1, 50),
  });

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: ['notifications'] });
    void queryClient.invalidateQueries({ queryKey: ['notifications', 'count'] });
    void queryClient.invalidateQueries({ queryKey: ['dashboard'] });
  };

  const markRead = useMutation({ mutationFn: api.markNotificationRead, onSuccess: invalidate });
  const markAll = useMutation({ mutationFn: api.markAllNotificationsRead, onSuccess: invalidate });

  if (notifications.isPending) {
    return <Loader />;
  }

  const items = notifications.data?.items ?? [];
  const unread = items.filter((item) => !item.readAt);

  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <Title order={2}>{t('notifications.title')}</Title>
        {unread.length > 0 && (
          <Button variant="light" size="xs" onClick={() => markAll.mutate()} loading={markAll.isPending}>
            {t('notifications.markAllRead')}
          </Button>
        )}
      </Group>

      {items.length === 0 ? (
        <Text c="dimmed">{t('notifications.empty')}</Text>
      ) : (
        <Stack gap="xs">
          {items.map((item) => (
            <Card key={item.id} withBorder padding="sm" bg={item.readAt ? undefined : 'var(--mantine-color-default-hover)'}>
              <Group justify="space-between" wrap="nowrap" align="flex-start">
                <Stack gap={2}>
                  <Group gap="xs">
                    <Text fw={item.readAt ? 400 : 600} size="sm">
                      {t(`notification.${item.kind}`)}
                    </Text>
                    {!item.readAt && <Badge size="xs" variant="light">{t('notifications.unread')}</Badge>}
                  </Group>

                  {item.targetPath && (
                    <Anchor component={Link} to={`/p/${encodePath(item.targetPath)}`} size="sm">
                      {titleOf(item) ?? item.targetPath}
                    </Anchor>
                  )}
                </Stack>

                <Stack gap={4} align="flex-end">
                  <Text size="xs" c="dimmed">
                    {formatRelative(item.createdAt, i18n.language)}
                  </Text>
                  {!item.readAt && (
                    <Button variant="subtle" size="compact-xs" onClick={() => markRead.mutate(item.id)}>
                      {t('notifications.markRead')}
                    </Button>
                  )}
                </Stack>
              </Group>
            </Card>
          ))}
        </Stack>
      )}
    </Stack>
  );
}

/**
 * The page title out of the payload, when there is one.
 *
 * The payload carries only what the inbox needs to render a line, and never anything the recipient
 * could not get by opening the page — so a malformed one costs a nicer label and nothing else.
 */
function titleOf(notification: Notification): string | null {
  if (!notification.payloadJson) {
    return null;
  }

  try {
    const payload = JSON.parse(notification.payloadJson) as { title?: string };
    return payload.title ?? null;
  } catch {
    return null;
  }
}
