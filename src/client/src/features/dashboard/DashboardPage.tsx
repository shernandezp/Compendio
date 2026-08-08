import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { Alert, Anchor, Badge, Card, Group, Loader, SimpleGrid, Stack, Text, Title } from '@mantine/core';

import { api, encodePath } from '../../lib/api';
import { formatRelative } from '../../lib/format';

/**
 * The landing screen: what you own, what of it has gone stale, and what you owe.
 *
 * Assembled by the server from the same three sources the dedicated screens use, so this cannot say
 * something different from the report it links to — a dashboard that disagrees with the page behind
 * it is worse than no dashboard.
 */
export function DashboardPage() {
  const { t, i18n } = useTranslation();
  const dashboard = useQuery({ queryKey: ['dashboard'], queryFn: api.dashboard });

  if (dashboard.isPending) {
    return <Loader />;
  }

  const data = dashboard.data;

  if (!data) {
    return <Text c="dimmed">—</Text>;
  }

  return (
    <Stack gap="lg">
      <Title order={2}>{t('dashboard.title')}</Title>

      {data.outstandingAcknowledgments.length > 0 && (
        <Alert color="orange" title={t('dashboard.acknowledgmentsOwed')}>
          <Stack gap="xs">
            {data.outstandingAcknowledgments.map((task) => (
              <Group key={task.path} gap="xs">
                <Anchor component={Link} to={`/p/${encodePath(task.path)}`}>
                  {task.title}
                </Anchor>
                {task.overdue && <Badge color="red" size="sm">{t('dashboard.overdue')}</Badge>}
              </Group>
            ))}
          </Stack>
        </Alert>
      )}

      <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
        <Card withBorder padding="md">
          <Stack gap="sm">
            <Group justify="space-between">
              <Title order={4}>{t('dashboard.myStalePages')}</Title>
              <Anchor component={Link} to="/stale" size="sm">
                {t('dashboard.viewAll')}
              </Anchor>
            </Group>

            {data.myStalePages.length === 0 ? (
              <Text c="dimmed" size="sm">
                {t('dashboard.nothingStale', { count: data.myPageCount })}
              </Text>
            ) : (
              data.myStalePages.map((page) => (
                <Group key={page.path} justify="space-between" wrap="nowrap">
                  <Anchor component={Link} to={`/p/${encodePath(page.path)}`} lineClamp={1}>
                    {page.title}
                  </Anchor>
                  <Badge color="yellow" size="sm">
                    {t('dashboard.daysOverdue', { count: page.daysOverdue ?? 0 })}
                  </Badge>
                </Group>
              ))
            )}
          </Stack>
        </Card>

        <Card withBorder padding="md">
          <Stack gap="sm">
            <Group justify="space-between">
              <Title order={4}>{t('dashboard.notifications')}</Title>
              <Anchor component={Link} to="/notifications" size="sm">
                {t('dashboard.viewAll')}
              </Anchor>
            </Group>

            {data.recentNotifications.length === 0 ? (
              <Text c="dimmed" size="sm">
                {t('dashboard.noNotifications')}
              </Text>
            ) : (
              data.recentNotifications.map((notification) => (
                <Group key={notification.id} justify="space-between" wrap="nowrap">
                  <Text size="sm" lineClamp={1}>
                    {t(`notification.${notification.kind}`)}
                    {notification.targetPath && ` · ${notification.targetPath}`}
                  </Text>
                  <Text size="xs" c="dimmed">
                    {formatRelative(notification.createdAt, i18n.language)}
                  </Text>
                </Group>
              ))
            )}
          </Stack>
        </Card>
      </SimpleGrid>
    </Stack>
  );
}
