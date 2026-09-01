import { useTranslation } from 'react-i18next';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Badge, Button, Card, Group, Loader, SimpleGrid, Stack, Table, Tabs, Text, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';

import { api } from '../../lib/api';
import { formatBytes, formatDate, formatNumber } from '../../lib/format';
import { SecureScopes } from './SecureScopes';
import { AiSettingsPanel, GitMirrorPanel } from './AiSettingsPanel';
import {
  AddGroupButton,
  AddPersonButton,
  CreateBackupButton,
  EditPersonButton,
  ManageGroupMembersButton,
  RenameGroupButton,
  RestoreDeletedPageButton,
} from './AdminActions';

/**
 * Administration.
 *
 * Everything here renders what the API returned; none of it decides anything. The access screen in
 * particular exists to make the permission model explainable — two states per folder, no deny
 * rules, and an effective-access preview that answers "why can this person see this" without
 * anybody having to trace it.
 */
export function AdminPage() {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();

  const status = useQuery({ queryKey: ['status'], queryFn: api.status });
  const users = useQuery({ queryKey: ['users'], queryFn: api.users });
  const groups = useQuery({ queryKey: ['groups'], queryFn: api.groups });
  const audit = useQuery({ queryKey: ['audit'], queryFn: () => api.auditLog() });
  const deleted = useQuery({ queryKey: ['deleted-pages'], queryFn: api.deletedPages });

  const reindex = useMutation({
    mutationFn: api.reindex,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['status'] });
      notifications.show({ message: t('admin.status.reindex') });
    },
  });

  const reconcile = useMutation({
    mutationFn: api.reconcile,
    onSuccess: async () => {
      await queryClient.invalidateQueries();
      notifications.show({ message: t('admin.status.reconcile') });
    },
  });

  return (
    <Stack gap="lg">
      <Title order={2}>{t('admin.title')}</Title>

      <Tabs defaultValue="status">
        <Tabs.List>
          <Tabs.Tab value="status">{t('admin.status.title')}</Tabs.Tab>
          <Tabs.Tab value="users">{t('admin.users')}</Tabs.Tab>
          <Tabs.Tab value="groups">{t('admin.groups')}</Tabs.Tab>
          <Tabs.Tab value="secure">{t('admin.secure')}</Tabs.Tab>
          <Tabs.Tab value="integrations">{t('admin.integrations')}</Tabs.Tab>
          <Tabs.Tab value="deleted">{t('admin.deleted.title')}</Tabs.Tab>
          <Tabs.Tab value="audit">{t('admin.audit.title')}</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="status" pt="md">
          {status.isPending ? (
            <Loader />
          ) : status.data ? (
            <Stack gap="md">
              <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }}>
                <Fact label={t('admin.status.version')} value={status.data.version} />
                <Fact label={t('admin.status.installMode')} value={status.data.installMode} />
                <Fact label={t('admin.status.contentRoot')} value={status.data.contentRoot} mono />
                <Fact label={t('admin.status.pages')} value={formatNumber(status.data.pageCount, i18n.language)} />
                <Fact label={t('admin.status.folders')} value={formatNumber(status.data.folderCount, i18n.language)} />
                <Fact label={t('admin.status.watcher')} value={status.data.watcherMode} />
                <Fact label={t('admin.status.index')} value={status.data.indexState} />
                <Fact
                  label={t('admin.status.queue')}
                  value={formatNumber(status.data.indexQueueDepth, i18n.language)}
                />
                <Fact
                  label={t('admin.status.database')}
                  value={formatBytes(status.data.databaseBytes, i18n.language)}
                />
                <Fact
                  label={t('admin.status.content')}
                  value={formatBytes(status.data.contentBytes, i18n.language)}
                />
                <Fact
                  label={t('admin.status.lastBackup')}
                  value={
                    status.data.lastBackupAt
                      ? formatDate(status.data.lastBackupAt, i18n.language)
                      : t('admin.status.neverBackedUp')
                  }
                />
                <Fact label={t('admin.secureScope.keyStatus')} value={status.data.secureAvailability} />
              </SimpleGrid>

              <Group>
                <Button onClick={() => reindex.mutate()} loading={reindex.isPending} variant="default">
                  {t('admin.status.reindex')}
                </Button>
                <Button onClick={() => reconcile.mutate()} loading={reconcile.isPending} variant="default">
                  {t('admin.status.reconcile')}
                </Button>
                <CreateBackupButton />
              </Group>
            </Stack>
          ) : null}
        </Tabs.Panel>

        <Tabs.Panel value="users" pt="md">
          <Stack gap="sm">
            <Group justify="space-between">
              <Text size="sm" c="dimmed">
                {t('admin.user.roleHelp')}
              </Text>
              <AddPersonButton />
            </Group>

            {users.isPending ? (
              <Loader />
            ) : (
              <Table striped highlightOnHover>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('auth.displayName')}</Table.Th>
                    <Table.Th>{t('auth.userName')}</Table.Th>
                    <Table.Th>{t('admin.user.role')}</Table.Th>
                    <Table.Th>{t('admin.user.active')}</Table.Th>
                    <Table.Th>{t('admin.user.lastSignIn')}</Table.Th>
                    <Table.Th />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {(users.data ?? []).map((user) => (
                    <Table.Tr key={user.id}>
                      <Table.Td>{user.displayName}</Table.Td>
                      <Table.Td>{user.userName}</Table.Td>
                      <Table.Td>
                        <Badge variant="light">{t(`admin.user.roles.${user.role}`)}</Badge>
                      </Table.Td>
                      <Table.Td>{user.active ? '✓' : '—'}</Table.Td>
                      <Table.Td>
                        {user.lastSignInAt
                          ? formatDate(user.lastSignInAt, i18n.language)
                          : t('admin.user.never')}
                      </Table.Td>
                      <Table.Td ta="right">
                        <EditPersonButton user={user} />
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="secure" pt="md">
          <SecureScopes />
        </Tabs.Panel>

        {/* Both optional, both off by default, and both reporting plainly when they are off. */}
        <Tabs.Panel value="integrations" pt="md">
          <Stack gap="lg">
            <AiSettingsPanel />
            <GitMirrorPanel />
          </Stack>
        </Tabs.Panel>

        {/* The recovery the guide promises and the version tombstones exist for. A page deleted from
            the tree — or by a mis-synced backup client — is listed here until its history ages out. */}
        <Tabs.Panel value="deleted" pt="md">
          <Stack gap="sm">
            <Text size="sm" c="dimmed">
              {t('admin.deleted.intro')}
            </Text>
            {deleted.isPending ? (
              <Loader />
            ) : (deleted.data ?? []).length === 0 ? (
              <Text size="sm" c="dimmed">
                {t('admin.deleted.empty')}
              </Text>
            ) : (
              <Table striped highlightOnHover>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('page.titleLabel')}</Table.Th>
                    <Table.Th>{t('admin.deleted.path')}</Table.Th>
                    <Table.Th>{t('admin.deleted.deletedAt')}</Table.Th>
                    <Table.Th>{t('admin.deleted.versions')}</Table.Th>
                    <Table.Th />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {(deleted.data ?? []).map((page) => (
                    <Table.Tr key={page.pageId}>
                      <Table.Td>{page.title}</Table.Td>
                      <Table.Td ff="monospace" style={{ wordBreak: 'break-all' }}>
                        {page.path}
                      </Table.Td>
                      <Table.Td>{formatDate(page.deletedAt, i18n.language)}</Table.Td>
                      <Table.Td>{formatNumber(page.versions, i18n.language)}</Table.Td>
                      <Table.Td ta="right">
                        <RestoreDeletedPageButton page={page} />
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="audit" pt="md">
          {audit.isPending ? (
            <Loader />
          ) : (
            <Table striped highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('admin.audit.when')}</Table.Th>
                  <Table.Th>{t('admin.audit.actor')}</Table.Th>
                  <Table.Th>{t('admin.audit.action')}</Table.Th>
                  <Table.Th>{t('admin.audit.target')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {(audit.data?.items ?? []).map((entry) => (
                  <Table.Tr key={entry.id}>
                    <Table.Td>{formatDate(entry.at, i18n.language)}</Table.Td>
                    <Table.Td>{entry.actorDisplayName ?? '—'}</Table.Td>
                    <Table.Td>
                      <Badge size="sm" variant="light">
                        {entry.action}
                      </Badge>
                    </Table.Td>
                    <Table.Td>{entry.targetPath}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          )}
        </Tabs.Panel>

        <Tabs.Panel value="groups" pt="md">
          {groups.isPending ? (
            <Loader />
          ) : (
            <Stack gap="xs">
              <Group justify="flex-end">
                <AddGroupButton />
              </Group>
              {(groups.data ?? []).map((group) => (
                <Card key={group.id} withBorder padding="sm">
                  <Group justify="space-between">
                    <div>
                      <Text fw={600}>{group.name}</Text>
                      <Text size="sm" c="dimmed">
                        {t('admin.group.memberCount', { count: group.memberIds.length })}
                      </Text>
                    </div>
                    <Group gap="xs">
                      <RenameGroupButton group={group} />
                      <ManageGroupMembersButton group={group} users={users.data ?? []} />
                    </Group>
                  </Group>
                </Card>
              ))}
            </Stack>
          )}
        </Tabs.Panel>
      </Tabs>
    </Stack>
  );
}

function Fact({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <Card withBorder padding="sm">
      <Text size="xs" c="dimmed" tt="uppercase" fw={700}>
        {label}
      </Text>
      <Text size="sm" mt={4} ff={mono ? 'monospace' : undefined} style={{ wordBreak: 'break-all' }}>
        {value}
      </Text>
    </Card>
  );
}
