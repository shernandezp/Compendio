import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Card, Container, PasswordInput, Select, Stack, TextInput, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';

import { api, ApiError } from '../../lib/api';
import { changeLanguage, SUPPORTED_LANGUAGES } from '../../i18n';

export function ProfilePage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const session = useQuery({ queryKey: ['session'], queryFn: api.session });

  const [displayName, setDisplayName] = useState('');
  const [email, setEmail] = useState('');
  const [language, setLanguage] = useState<string | null>(null);
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [passwordError, setPasswordError] = useState<string | null>(null);

  useEffect(() => {
    if (session.data?.user) {
      setDisplayName(session.data.user.displayName);
      setEmail(session.data.user.email ?? '');
      setLanguage(session.data.user.preferredLanguage ?? null);
    }
  }, [session.data]);

  async function saveProfile() {
    await api.updateProfile({ displayName, email, preferredLanguage: language ?? '' });

    // The preference wins over every other step of the resolution chain, so apply it now rather
    // than at the next page load.
    if (language) {
      changeLanguage(language);
    }

    await queryClient.invalidateQueries({ queryKey: ['session'] });
    notifications.show({ message: t('auth.profileSaved') });
  }

  async function savePassword() {
    setPasswordError(null);

    try {
      await api.changePassword(currentPassword, newPassword);
      setCurrentPassword('');
      setNewPassword('');
      notifications.show({ message: t('auth.passwordChanged') });
    } catch (error) {
      setPasswordError(error instanceof ApiError ? error.detail || error.title : t('app.error.generic'));
    }
  }

  return (
    <Container size="sm">
      <Stack gap="lg">
        <Title order={2}>{t('auth.profile')}</Title>

        <Card withBorder padding="lg">
          <Stack>
            <TextInput
              label={t('auth.displayName')}
              value={displayName}
              onChange={(event) => setDisplayName(event.currentTarget.value)}
            />
            <TextInput
              label={t('auth.email')}
              value={email}
              onChange={(event) => setEmail(event.currentTarget.value)}
              type="email"
            />
            <Select
              label={t('app.language.label')}
              value={language}
              onChange={setLanguage}
              data={SUPPORTED_LANGUAGES.map((code) => ({
                value: code,
                label: code === 'es' ? 'Español' : 'English',
              }))}
              clearable
            />
            <Button onClick={() => void saveProfile()}>{t('app.save')}</Button>
          </Stack>
        </Card>

        <Card withBorder padding="lg">
          <Stack>
            <Title order={4}>{t('auth.changePassword')}</Title>
            <PasswordInput
              label={t('auth.currentPassword')}
              value={currentPassword}
              onChange={(event) => setCurrentPassword(event.currentTarget.value)}
              autoComplete="current-password"
            />
            <PasswordInput
              label={t('auth.newPassword')}
              value={newPassword}
              onChange={(event) => setNewPassword(event.currentTarget.value)}
              error={passwordError}
              autoComplete="new-password"
            />
            <Button onClick={() => void savePassword()} disabled={newPassword.length < 12}>
              {t('auth.changePassword')}
            </Button>
          </Stack>
        </Card>
      </Stack>
    </Container>
  );
}
