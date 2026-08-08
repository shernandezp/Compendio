import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Group,
  Modal,
  MultiSelect,
  PasswordInput,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconDatabaseExport, IconPencil, IconUserPlus, IconUsers, IconUsersPlus } from '@tabler/icons-react';

import { ApiError, api, type Group as GroupModel, type User, type UserRole } from '../../lib/api';

/**
 * The three "create" actions the administration screen was missing: a person, a group, and a
 * backup. Each is a self-contained button-and-modal so the tables in {@link AdminPage} stay read-only
 * lists and the mutation logic lives next to the form it belongs to.
 */

function reportError(fallback: string) {
  return (error: unknown) => {
    const message = error instanceof ApiError && error.detail ? error.detail : fallback;
    notifications.show({ color: 'red', message });
  };
}

/** Add a person. The backend enforces the role ceiling and password rules; this only collects them. */
export function AddPersonButton() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const [opened, setOpened] = useState(false);
  const [userName, setUserName] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState<UserRole>('Reader');

  function reset() {
    setUserName('');
    setDisplayName('');
    setEmail('');
    setPassword('');
    setRole('Reader');
  }

  const create = useMutation({
    mutationFn: () =>
      api.createUser({
        userName: userName.trim(),
        displayName: displayName.trim(),
        email: email.trim() || null,
        password,
        role,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['users'] });
      notifications.show({ message: t('admin.user.created') });
      setOpened(false);
      reset();
    },
    onError: reportError(t('app.error.generic')),
  });

  const valid = userName.trim().length > 0 && displayName.trim().length > 0 && password.length >= 12;

  return (
    <>
      <Button leftSection={<IconUserPlus size={16} />} onClick={() => setOpened(true)}>
        {t('admin.user.add')}
      </Button>

      <Modal opened={opened} onClose={() => setOpened(false)} title={t('admin.user.add')}>
        <Stack>
          <TextInput
            label={t('auth.userName')}
            value={userName}
            onChange={(event) => setUserName(event.currentTarget.value)}
            data-autofocus
            required
          />
          <TextInput
            label={t('auth.displayName')}
            value={displayName}
            onChange={(event) => setDisplayName(event.currentTarget.value)}
            required
          />
          <TextInput
            label={t('auth.email')}
            value={email}
            onChange={(event) => setEmail(event.currentTarget.value)}
            type="email"
          />
          <PasswordInput
            label={t('auth.password')}
            description={t('setup.account.passwordHint')}
            value={password}
            onChange={(event) => setPassword(event.currentTarget.value)}
            required
          />
          <Select
            label={t('admin.user.role')}
            value={role}
            onChange={(value) => setRole((value as UserRole) ?? 'Reader')}
            data={[
              { value: 'Reader', label: t('admin.user.roles.Reader') },
              { value: 'Editor', label: t('admin.user.roles.Editor') },
              { value: 'Admin', label: t('admin.user.roles.Admin') },
            ]}
            allowDeselect={false}
          />
          <Text size="sm" c="dimmed">
            {t('admin.user.roleHelp')}
          </Text>

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>
              {t('app.cancel')}
            </Button>
            <Button onClick={() => create.mutate()} loading={create.isPending} disabled={!valid}>
              {t('admin.user.add')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/**
 * Edit a person.
 *
 * @remarks
 * Display name, email, role and active state go through one save; a new password, when typed, goes
 * through its own endpoint because it must never travel or be logged beside the rest of the profile.
 * The last-administrator rule is the server's to enforce — demoting or deactivating the only admin
 * comes back as `acl.last_admin`, and its localized detail is what the error surfaces, so the UI
 * does not keep its own copy of a rule that has to hold on every path.
 */
export function EditPersonButton({ user }: { user: User }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const [opened, setOpened] = useState(false);
  const [displayName, setDisplayName] = useState(user.displayName);
  const [email, setEmail] = useState(user.email ?? '');
  const [role, setRole] = useState<UserRole>(user.role);
  const [active, setActive] = useState(user.active);
  const [password, setPassword] = useState('');

  function open() {
    // Re-seed from the user each time, so a cancelled edit leaves nothing behind.
    setDisplayName(user.displayName);
    setEmail(user.email ?? '');
    setRole(user.role);
    setActive(user.active);
    setPassword('');
    setOpened(true);
  }

  const save = useMutation({
    mutationFn: async () => {
      await api.updateUser(user.id, {
        displayName: displayName.trim(),
        email: email.trim() || null,
        role,
        active,
      });
      // Only when one was typed, and always last: a rejected profile change must not leave a new
      // password behind it.
      if (password.length > 0) {
        await api.setUserPassword(user.id, password);
      }
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['users'] });
      notifications.show({ message: t('admin.user.saved') });
      setOpened(false);
    },
    onError: reportError(t('app.error.generic')),
  });

  const valid = displayName.trim().length > 0 && (password.length === 0 || password.length >= 12);

  return (
    <>
      <Button size="xs" variant="default" leftSection={<IconPencil size={14} />} onClick={open}>
        {t('app.edit')}
      </Button>

      <Modal opened={opened} onClose={() => setOpened(false)} title={`${t('app.edit')} — ${user.userName}`}>
        <Stack>
          <TextInput
            label={t('auth.displayName')}
            value={displayName}
            onChange={(event) => setDisplayName(event.currentTarget.value)}
            data-autofocus
            required
          />
          <TextInput
            label={t('auth.email')}
            value={email}
            onChange={(event) => setEmail(event.currentTarget.value)}
            type="email"
          />
          <Select
            label={t('admin.user.role')}
            value={role}
            onChange={(value) => setRole((value as UserRole) ?? role)}
            data={[
              { value: 'Reader', label: t('admin.user.roles.Reader') },
              { value: 'Editor', label: t('admin.user.roles.Editor') },
              { value: 'Admin', label: t('admin.user.roles.Admin') },
            ]}
            allowDeselect={false}
          />
          <Switch
            label={t('admin.user.active')}
            checked={active}
            onChange={(event) => setActive(event.currentTarget.checked)}
          />
          <PasswordInput
            label={t('admin.user.resetPassword')}
            description={t('setup.account.passwordHint')}
            placeholder={t('admin.user.leaveBlankPassword')}
            value={password}
            onChange={(event) => setPassword(event.currentTarget.value)}
          />

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>
              {t('app.cancel')}
            </Button>
            <Button onClick={() => save.mutate()} loading={save.isPending} disabled={!valid}>
              {t('app.save')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/** Add a group. Members are assigned afterwards through the access screen. */
export function AddGroupButton() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const [opened, setOpened] = useState(false);
  const [name, setName] = useState('');

  const create = useMutation({
    mutationFn: () => api.createGroup(name.trim()),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['groups'] });
      notifications.show({ message: t('admin.group.created') });
      setOpened(false);
      setName('');
    },
    onError: reportError(t('app.error.generic')),
  });

  return (
    <>
      <Button leftSection={<IconUsersPlus size={16} />} onClick={() => setOpened(true)}>
        {t('admin.group.add')}
      </Button>

      <Modal opened={opened} onClose={() => setOpened(false)} title={t('admin.group.add')}>
        <Stack>
          <TextInput
            label={t('admin.group.name')}
            value={name}
            onChange={(event) => setName(event.currentTarget.value)}
            data-autofocus
            required
          />

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>
              {t('app.cancel')}
            </Button>
            <Button onClick={() => create.mutate()} loading={create.isPending} disabled={name.trim().length === 0}>
              {t('admin.group.add')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/** Rename a group. */
export function RenameGroupButton({ group }: { group: GroupModel }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const [opened, setOpened] = useState(false);
  const [name, setName] = useState(group.name);

  function open() {
    setName(group.name);
    setOpened(true);
  }

  const save = useMutation({
    mutationFn: () => api.updateGroup(group.id, { name: name.trim() }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['groups'] });
      notifications.show({ message: t('admin.group.renamed') });
      setOpened(false);
    },
    onError: reportError(t('app.error.generic')),
  });

  return (
    <>
      <Button size="xs" variant="default" leftSection={<IconPencil size={14} />} onClick={open}>
        {t('admin.group.rename')}
      </Button>

      <Modal opened={opened} onClose={() => setOpened(false)} title={t('admin.group.rename')} size="sm">
        <Stack>
          <TextInput
            label={t('admin.group.name')}
            value={name}
            onChange={(event) => setName(event.currentTarget.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && name.trim().length > 0) {
                save.mutate();
              }
            }}
            data-autofocus
            required
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>
              {t('app.cancel')}
            </Button>
            <Button onClick={() => save.mutate()} loading={save.isPending} disabled={name.trim().length === 0}>
              {t('app.save')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/**
 * Manage a group's members.
 *
 * The full membership set is sent on save — the API replaces it wholesale rather than diffing — so
 * the multi-select is the source of truth: whatever is selected is exactly who ends up in the group.
 * Deactivated accounts are left out of the options, since adding one back to a group would be a
 * quiet way to keep granting access to somebody who has been switched off.
 */
export function ManageGroupMembersButton({ group, users }: { group: GroupModel; users: User[] }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const [opened, setOpened] = useState(false);
  const [memberIds, setMemberIds] = useState<string[]>(group.memberIds);

  function open() {
    // Re-seed from the group each time it opens, so a cancelled edit does not linger.
    setMemberIds(group.memberIds);
    setOpened(true);
  }

  const options = users
    .filter((user) => user.active || group.memberIds.includes(user.id))
    .map((user) => ({ value: user.id, label: `${user.displayName} (${user.userName})` }));

  const save = useMutation({
    mutationFn: () => api.updateGroup(group.id, { memberIds }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['groups'] });
      notifications.show({ message: t('admin.group.membersSaved') });
      setOpened(false);
    },
    onError: reportError(t('app.error.generic')),
  });

  return (
    <>
      <Button size="xs" variant="default" leftSection={<IconUsers size={14} />} onClick={open}>
        {t('admin.group.manageMembers')}
      </Button>

      <Modal opened={opened} onClose={() => setOpened(false)} title={`${t('admin.group.members')} — ${group.name}`}>
        <Stack>
          <MultiSelect
            label={t('admin.group.members')}
            placeholder={t('admin.group.membersPlaceholder')}
            data={options}
            value={memberIds}
            onChange={setMemberIds}
            searchable
            clearable
            nothingFoundMessage={t('admin.group.noPeople')}
            data-autofocus
          />

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>
              {t('app.cancel')}
            </Button>
            <Button onClick={() => save.mutate()} loading={save.isPending}>
              {t('app.save')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}

/**
 * Create a backup on the server.
 *
 * When any folder is encrypted the archive has to carry the master key rewrapped under a passphrase,
 * so the button opens a short modal that asks for one; with nothing encrypted it runs straight away.
 * Either way the archive lands in the server's backups folder, and the last-backup time on the status
 * screen refreshes when it is done.
 */
export function CreateBackupButton() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const scopes = useQuery({ queryKey: ['secure-scopes'], queryFn: api.secureScopes });
  const needsPassphrase = (scopes.data ?? []).length > 0;

  const [opened, setOpened] = useState(false);
  const [passphrase, setPassphrase] = useState('');

  const backup = useMutation({
    mutationFn: (value: string | undefined) => api.createBackup(value),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ['status'] });
      notifications.show({ message: t('admin.status.backupDone', { file: result.fileName }) });
      setOpened(false);
      setPassphrase('');
    },
    onError: reportError(t('app.error.generic')),
  });

  function onClick() {
    if (needsPassphrase) {
      setOpened(true);
      return;
    }
    backup.mutate(undefined);
  }

  return (
    <>
      <Button
        leftSection={<IconDatabaseExport size={16} />}
        variant="default"
        onClick={onClick}
        loading={backup.isPending && !opened}
      >
        {t('admin.status.backup')}
      </Button>

      <Modal opened={opened} onClose={() => setOpened(false)} title={t('admin.status.backup')}>
        <Stack>
          <Alert>{t('admin.status.backupPassphraseHint')}</Alert>
          <PasswordInput
            label={t('admin.status.backupPassphrase')}
            value={passphrase}
            onChange={(event) => setPassphrase(event.currentTarget.value)}
            data-autofocus
            required
          />

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>
              {t('app.cancel')}
            </Button>
            <Button
              onClick={() => backup.mutate(passphrase)}
              loading={backup.isPending}
              disabled={passphrase.length === 0}
            >
              {t('admin.status.backup')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}
