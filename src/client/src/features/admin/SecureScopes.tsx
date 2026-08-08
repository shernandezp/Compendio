import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Badge,
  Button,
  Card,
  Checkbox,
  Group,
  Loader,
  Modal,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { modals } from '@mantine/modals';
import { notifications } from '@mantine/notifications';
import { IconAlertTriangle, IconLock } from '@tabler/icons-react';

import { api } from '../../lib/api';
import { useAiStatus } from '../ai/useAi';

/**
 * Encrypted folders.
 *
 * The threat model is stated on this screen, in one sentence, before anything is encrypted.
 * Overstating what encryption buys is worse than not having it: this protects a stolen disk, a
 * backup archive and a mis-synced folder, and it does not protect against an administrator of the
 * server or hide any file name.
 */
export function SecureScopes() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const scopes = useQuery({ queryKey: ['secure-scopes'], queryFn: api.secureScopes });
  const ai = useAiStatus();

  const [opened, setOpened] = useState(false);
  const [path, setPath] = useState('');
  const [understood, setUnderstood] = useState(false);
  const [busy, setBusy] = useState(false);

  async function create() {
    setBusy(true);
    try {
      await api.createSecureScope(path, false, false);
      await queryClient.invalidateQueries();
      notifications.show({ message: t('admin.secureScope.created') });
      setOpened(false);
      setPath('');
      setUnderstood(false);
    } catch {
      notifications.show({ color: 'red', message: t('app.error.generic') });
    } finally {
      setBusy(false);
    }
  }

  async function toggleIndexing(folderPath: string, indexContent: boolean) {
    await api.updateSecureScope(folderPath, { indexContent });
    await queryClient.invalidateQueries({ queryKey: ['secure-scopes'] });
  }

  /**
   * Lets the AI assistant read inside an encrypted folder.
   *
   * @remarks
   * Off at creation and only ever turned on here, deliberately. Until this existed the refusal an
   * editor saw — "encrypted folders are excluded until an administrator opts them in" — described a
   * control that did not exist, which is worse than having no answer at all.
   *
   * The endpoint is named in the confirmation rather than in a tooltip, because "the contents of
   * this folder will be sent to <host>" is the entire decision, and an administrator who encrypted a
   * folder has already said what they think of its contents leaving the building.
   */
  async function setAllowAi(folderPath: string, allowAi: boolean) {
    await api.updateSecureScope(folderPath, { allowAi });
    await queryClient.invalidateQueries({ queryKey: ['secure-scopes'] });
  }

  function toggleAi(folderPath: string, allowAi: boolean) {
    // Turning it off needs no ceremony — it only ever narrows what leaves the building.
    if (!allowAi) {
      void setAllowAi(folderPath, false);
      return;
    }

    modals.openConfirmModal({
      title: t('admin.secureScope.allowAi'),
      children: (
        <Text size="sm">{t('admin.secureScope.allowAiConfirm', { endpoint: ai.endpointLabel })}</Text>
      ),
      labels: { confirm: t('app.confirm'), cancel: t('app.cancel') },
      confirmProps: { color: 'orange' },
      onConfirm: () => void setAllowAi(folderPath, true),
    });
  }

  return (
    <Stack gap="md">
      <Group justify="space-between">
        <Title order={4}>{t('admin.secure')}</Title>
        <Button leftSection={<IconLock size={16} />} onClick={() => setOpened(true)}>
          {t('admin.secureScope.add')}
        </Button>
      </Group>

      <Alert variant="light" icon={<IconAlertTriangle size={18} />}>
        {t('admin.secureScope.threatModel')}
      </Alert>

      <Text size="sm" c="dimmed">
        {t('admin.secureScope.filesFirstSuspended')}
      </Text>

      {scopes.isPending ? (
        <Loader size="sm" />
      ) : (scopes.data ?? []).length === 0 ? (
        <Text size="sm" c="dimmed">
          —
        </Text>
      ) : (
        (scopes.data ?? []).map((scope) => (
          <Card key={scope.folderPath} withBorder padding="md">
            <Group justify="space-between" align="flex-start">
              <div>
                <Group gap="xs">
                  <Text fw={600}>{scope.folderPath}</Text>
                  <Badge
                    size="sm"
                    variant="light"
                    color={scope.availability === 'Available' ? 'green' : 'red'}
                  >
                    {scope.availability === 'Available'
                      ? t('admin.secureScope.available')
                      : t('admin.secureScope.unavailable')}
                  </Badge>
                </Group>
                <Text size="xs" c="dimmed" mt={4}>
                  {t('admin.secureScope.keyStatus')}: {scope.keyId}
                </Text>
              </div>

              <Stack gap="xs">
                <Switch
                  label={t('admin.secureScope.indexContent')}
                  checked={scope.indexContent}
                  onChange={(event) => void toggleIndexing(scope.folderPath, event.currentTarget.checked)}
                />
                {!scope.indexContent && (
                  <Text size="xs" c="dimmed" maw={360}>
                    {t('admin.secureScope.indexContentWarning')}
                  </Text>
                )}

                {/* Only where it can mean anything. With no provider configured this would be a
                    switch that changes nothing observable, on the screen where that is least
                    forgivable. */}
                {ai.enabled && (
                  <>
                    <Switch
                      label={t('admin.secureScope.allowAi')}
                      checked={scope.allowAi}
                      onChange={(event) => toggleAi(scope.folderPath, event.currentTarget.checked)}
                    />
                    <Text size="xs" c="dimmed" maw={360}>
                      {scope.allowAi
                        ? t('admin.secureScope.allowAiOn', { endpoint: ai.endpointLabel })
                        : t('admin.secureScope.allowAiOff')}
                    </Text>
                  </>
                )}
              </Stack>
            </Group>
          </Card>
        ))
      )}

      <Modal opened={opened} onClose={() => setOpened(false)} title={t('admin.secureScope.add')}>
        <Stack>
          <TextInput
            label={t('admin.secureScope.path')}
            value={path}
            onChange={(event) => setPath(event.currentTarget.value)}
            placeholder="IT/Credenciales"
            data-autofocus
          />

          <Alert color="orange" icon={<IconAlertTriangle size={18} />}>
            {t('admin.secureScope.threatModel')}
          </Alert>

          <Text size="sm">{t('admin.secureScope.filesFirstSuspended')}</Text>

          {/* An explicit acknowledgement, because this is not reversible by pressing undo: the
              files on disk become envelopes and stop opening in an editor. */}
          <Checkbox
            label={t('app.confirm')}
            checked={understood}
            onChange={(event) => setUnderstood(event.currentTarget.checked)}
          />

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setOpened(false)}>
              {t('app.cancel')}
            </Button>
            <Button onClick={() => void create()} loading={busy} disabled={!understood || path.trim().length === 0}>
              {t('admin.secureScope.add')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
