import { useEffect, useState } from 'react';
import { useLocation } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Badge,
  Button,
  Card,
  Group,
  Loader,
  Radio,
  Select,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconInfoCircle, IconLock, IconTrash } from '@tabler/icons-react';

import { api, type AclEntry, type PermissionLevel } from '../../lib/api';

const LEVELS: PermissionLevel[] = ['Read', 'Write', 'Manage'];

/**
 * Access to one folder.
 *
 * Two states and no third: inherit — which can only *add* access — or restricted, which is exactly
 * the list below. There are no deny rules, and the screen says so rather than leaving somebody
 * hunting for the option.
 *
 * The effective-access preview is the reason this screen is worth building: it turns "why can Ana
 * see this?" from a support ticket into a dropdown.
 */
export function AccessEditor() {
  const { t } = useTranslation();
  const location = useLocation();
  const queryClient = useQueryClient();

  const path = decodeURIComponent(location.pathname.replace(/^\/admin\/access\/?/, ''));

  const acl = useQuery({ queryKey: ['acl', path], queryFn: () => api.acl(path), enabled: path.length > 0 });
  const users = useQuery({ queryKey: ['users'], queryFn: api.users });
  const groups = useQuery({ queryKey: ['groups'], queryFn: api.groups });

  const [inheritParent, setInheritParent] = useState(true);
  const [entries, setEntries] = useState<AclEntry[]>([]);
  const [previewUser, setPreviewUser] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (acl.data) {
      setInheritParent(acl.data.inheritParent);
      setEntries(acl.data.entries);
    }
  }, [acl.data]);

  const preview = useQuery({
    queryKey: ['effective', path, previewUser],
    queryFn: () => api.effectiveAccess(path, previewUser!),
    enabled: Boolean(previewUser) && path.length > 0,
  });

  async function save() {
    setSaving(true);
    try {
      await api.setAcl(
        path,
        inheritParent,
        entries.map((entry) => ({
          subjectType: entry.subjectType,
          subjectId: entry.subjectId,
          level: entry.level,
        })),
      );

      await queryClient.invalidateQueries({ queryKey: ['acl', path] });
      await queryClient.invalidateQueries({ queryKey: ['tree'] });
      notifications.show({ message: t('admin.acl.saved') });
    } catch {
      notifications.show({ color: 'red', message: t('app.error.generic') });
    } finally {
      setSaving(false);
    }
  }

  function addSubject(value: string | null) {
    if (!value) {
      return;
    }

    const [subjectType, subjectId] = value.split(':') as [AclEntry['subjectType'], string];
    if (entries.some((e) => e.subjectType === subjectType && (e.subjectId ?? '') === subjectId)) {
      return;
    }

    const subjectName =
      subjectType === 'Everyone'
        ? t('admin.acl.everyone')
        : subjectType === 'User'
          ? (users.data?.find((u) => u.id === subjectId)?.displayName ?? subjectId)
          : (groups.data?.find((g) => g.id === subjectId)?.name ?? subjectId);

    setEntries((current) => [
      ...current,
      { subjectType, subjectId: subjectType === 'Everyone' ? undefined : subjectId, subjectName, level: 'Read' },
    ]);
  }

  if (path.length === 0) {
    return <Alert variant="light">{t('admin.acl.title', { path: '/' })}</Alert>;
  }

  if (acl.isPending) {
    return <Loader />;
  }

  const parent = path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : null;

  return (
    <Stack gap="lg">
      <Title order={3}>{t('admin.acl.title', { path })}</Title>

      {acl.data?.isSecure && (
        <Alert color="orange" icon={<IconLock size={18} />}>
          {t('page.secureHint')}
        </Alert>
      )}

      <Card withBorder padding="lg">
        <Radio.Group value={inheritParent ? 'inherit' : 'restricted'} onChange={(v) => setInheritParent(v === 'inherit')}>
          <Stack gap="md">
            <Radio
              value="inherit"
              label={parent ? t('admin.acl.inherit', { parent }) : t('admin.acl.inheritRoot')}
              description={
                acl.data && acl.data.inheritedFrom.length > 0
                  ? t('admin.acl.currently', {
                      summary: acl.data.inheritedFrom
                        .map((e) => `${e.subjectName}: ${t(`admin.acl.levels.${e.level}`)}`)
                        .join(' · '),
                    })
                  : undefined
              }
            />
            <Radio value="restricted" label={t('admin.acl.restricted')} />
          </Stack>
        </Radio.Group>

        <Alert variant="light" mt="md" icon={<IconInfoCircle size={16} />}>
          {t('admin.acl.noDeny')}
        </Alert>
      </Card>

      <Card withBorder padding="lg">
        <Stack>
          <Select
            label={t('admin.acl.addSubject')}
            placeholder={t('admin.acl.addSubject')}
            value={null}
            onChange={addSubject}
            searchable
            data={[
              { value: 'Everyone:', label: t('admin.acl.everyone') },
              {
                group: t('admin.users'),
                items: (users.data ?? []).map((u) => ({ value: `User:${u.id}`, label: u.displayName })),
              },
              {
                group: t('admin.groups'),
                items: (groups.data ?? []).map((g) => ({ value: `Group:${g.id}`, label: g.name })),
              },
            ]}
          />

          <Table>
            <Table.Tbody>
              {entries.map((entry, index) => (
                <Table.Tr key={`${entry.subjectType}:${entry.subjectId ?? ''}`}>
                  <Table.Td>
                    <Group gap="xs">
                      <Text size="sm">{entry.subjectName}</Text>
                      <Badge size="xs" variant="light">
                        {entry.subjectType}
                      </Badge>
                    </Group>
                  </Table.Td>
                  <Table.Td w={180}>
                    <Select
                      size="xs"
                      value={entry.level}
                      onChange={(level) =>
                        setEntries((current) =>
                          current.map((e, i) => (i === index ? { ...e, level: (level as PermissionLevel) ?? 'Read' } : e)),
                        )
                      }
                      data={LEVELS.map((level) => ({ value: level, label: t(`admin.acl.levels.${level}`) }))}
                      aria-label={t('admin.user.role')}
                    />
                  </Table.Td>
                  <Table.Td w={44}>
                    <Button
                      size="compact-xs"
                      variant="subtle"
                      color="red"
                      onClick={() => setEntries((current) => current.filter((_, i) => i !== index))}
                      aria-label={t('app.delete')}
                    >
                      <IconTrash size={14} />
                    </Button>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>

          <Group justify="flex-end">
            <Button onClick={() => void save()} loading={saving}>
              {t('app.save')}
            </Button>
          </Group>
        </Stack>
      </Card>

      <Card withBorder padding="lg">
        <Stack>
          <Title order={5}>{t('admin.acl.preview')}</Title>
          <Select
            label={t('admin.acl.previewFor')}
            value={previewUser}
            onChange={setPreviewUser}
            searchable
            data={(users.data ?? []).map((u) => ({ value: u.id, label: u.displayName }))}
          />

          {preview.data && (
            <Alert variant="light">
              {t('admin.acl.previewResult', {
                name: preview.data.displayName,
                level: t(`admin.acl.levels.${preview.data.level}`),
              })}{' '}
              {/* The reason is a machine code with a known set of values; anything unrecognized
                  falls back to showing the code itself rather than an empty sentence. */}
              <Text component="span" c="dimmed">
                {t(`admin.acl.reason.${preview.data.reason.split(':')[0]}`, { defaultValue: preview.data.reason })}
              </Text>
            </Alert>
          )}
        </Stack>
      </Card>
    </Stack>
  );
}
