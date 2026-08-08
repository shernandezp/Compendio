import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Card, Center, Checkbox, PasswordInput, Stack, Text, TextInput, Title } from '@mantine/core';

import { api, ApiError } from '../../lib/api';

export function LoginPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [persistent, setPersistent] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    setBusy(true);
    setFailure(null);

    try {
      await api.login(userName, password, persistent);
      await queryClient.invalidateQueries();
      window.location.assign('/');
    } catch (error) {
      // One message whatever went wrong. Telling somebody the user name was right would turn this
      // form into a way to enumerate accounts.
      setFailure(error instanceof ApiError ? error.detail || t('auth.failed') : t('auth.failed'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Center h="100vh" p="md">
      <Card withBorder padding="xl" w={400} maw="100%">
        <form onSubmit={submit}>
          <Stack>
            <div>
              <Title order={2}>{t('app.name')}</Title>
              <Text size="sm" c="dimmed">
                {t('app.tagline')}
              </Text>
            </div>

            {failure && <Alert color="red">{failure}</Alert>}

            <TextInput
              label={t('auth.userName')}
              value={userName}
              onChange={(e) => setUserName(e.currentTarget.value)}
              autoComplete="username"
              required
              autoFocus
            />

            <PasswordInput
              label={t('auth.password')}
              value={password}
              onChange={(e) => setPassword(e.currentTarget.value)}
              autoComplete="current-password"
              required
            />

            <Checkbox
              label={t('auth.rememberMe')}
              checked={persistent}
              onChange={(e) => setPersistent(e.currentTarget.checked)}
            />

            <Button type="submit" loading={busy} fullWidth>
              {t('auth.signIn')}
            </Button>
          </Stack>
        </form>
      </Card>
    </Center>
  );
}
