import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Card,
  Code,
  Container,
  Group,
  PasswordInput,
  Radio,
  Stack,
  Stepper,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { IconAlertCircle, IconFolder } from '@tabler/icons-react';

import { api, ApiError } from '../../lib/api';
import { changeLanguage } from '../../i18n';

/**
 * The first-run wizard.
 *
 * The first control is the language picker, before the administrator account — a wizard that is
 * English-only sets the tone before the product is even installed.
 */
export function SetupWizard() {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const state = useQuery({ queryKey: ['setup'], queryFn: api.setupState });

  const [step, setStep] = useState(0);
  const [language, setLanguage] = useState(i18n.language);
  const [instanceName, setInstanceName] = useState('');
  const [userName, setUserName] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [defaultAccess, setDefaultAccess] = useState<'Read' | 'None'>('Read');
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  function pickLanguage(code: string) {
    setLanguage(code);
    changeLanguage(code);
  }

  async function finish() {
    setBusy(true);
    setFailure(null);
    setErrors({});

    try {
      await api.completeSetup({
        language,
        adminUserName: userName,
        adminPassword: password,
        adminDisplayName: displayName || userName,
        instanceName: instanceName || undefined,
        defaultAccess,
      });

      await api.login(userName, password, true);
      await queryClient.invalidateQueries();
      window.location.assign('/');
    } catch (error) {
      if (error instanceof ApiError) {
        setErrors(error.fieldErrors);
        setFailure(error.detail || error.title);
        if (Object.keys(error.fieldErrors).some((k) => k.startsWith('admin'))) {
          setStep(1);
        }
      } else {
        setFailure(t('app.error.generic'));
      }
    } finally {
      setBusy(false);
    }
  }

  const accountValid = userName.trim().length > 0 && password.length >= 12;

  return (
    <Container size="sm" py="xl">
      <Stack gap="lg">
        <div>
          <Title order={1}>{t('setup.title')}</Title>
          <Text c="dimmed" mt="xs">
            {t('setup.subtitle')}
          </Text>
        </div>

        {failure && (
          <Alert color="red" icon={<IconAlertCircle size={18} />} title={t('app.error.title')}>
            {failure}
          </Alert>
        )}

        <Card withBorder padding="lg">
          <Stepper active={step} onStepClick={setStep} size="sm" allowNextStepsSelect={false}>
            <Stepper.Step label={t('setup.steps.language')}>
              <Stack mt="lg">
                <Title order={3}>{t('setup.language.heading')}</Title>
                <Text size="sm" c="dimmed">
                  {t('setup.language.help')}
                </Text>

                <Radio.Group value={language} onChange={pickLanguage}>
                  <Stack gap="xs" mt="sm">
                    {(state.data?.languages ?? []).map((l) => (
                      <Radio key={l.code} value={l.code} label={l.nativeName} />
                    ))}
                  </Stack>
                </Radio.Group>
              </Stack>
            </Stepper.Step>

            <Stepper.Step label={t('setup.steps.account')}>
              <Stack mt="lg">
                <Title order={3}>{t('setup.account.heading')}</Title>
                <Text size="sm" c="dimmed">
                  {t('setup.account.help')}
                </Text>

                <TextInput
                  label={t('setup.account.instanceName')}
                  value={instanceName}
                  onChange={(e) => setInstanceName(e.currentTarget.value)}
                  placeholder={t('app.name')}
                />

                <TextInput
                  label={t('auth.userName')}
                  value={userName}
                  onChange={(e) => setUserName(e.currentTarget.value)}
                  error={errors.adminUserName?.[0]}
                  required
                  autoComplete="username"
                />

                <TextInput
                  label={t('auth.displayName')}
                  value={displayName}
                  onChange={(e) => setDisplayName(e.currentTarget.value)}
                />

                <PasswordInput
                  label={t('auth.password')}
                  description={t('setup.account.passwordHint')}
                  value={password}
                  onChange={(e) => setPassword(e.currentTarget.value)}
                  error={errors.adminPassword?.[0]}
                  required
                  autoComplete="new-password"
                />
              </Stack>
            </Stepper.Step>

            <Stepper.Step label={t('setup.steps.content')}>
              <Stack mt="lg">
                <Title order={3}>{t('setup.content.heading')}</Title>
                <Text size="sm" c="dimmed">
                  {t('setup.content.help')}
                </Text>

                <Group gap="xs" mt="sm">
                  <IconFolder size={18} />
                  <Code>{state.data?.contentRoot}</Code>
                </Group>
              </Stack>
            </Stepper.Step>

            <Stepper.Step label={t('setup.steps.access')}>
              <Stack mt="lg">
                <Title order={3}>{t('setup.access.heading')}</Title>

                <Radio.Group value={defaultAccess} onChange={(v) => setDefaultAccess(v as 'Read' | 'None')}>
                  <Stack gap="md" mt="sm">
                    <Radio
                      value="Read"
                      label={t('setup.access.everyone')}
                      description={t('setup.access.everyoneHelp')}
                    />
                    <Radio value="None" label={t('setup.access.nobody')} description={t('setup.access.nobodyHelp')} />
                  </Stack>
                </Radio.Group>

                <Alert mt="md" variant="light">
                  {t('setup.https.help')}
                </Alert>
              </Stack>
            </Stepper.Step>
          </Stepper>

          <Group justify="space-between" mt="xl">
            <Button variant="default" onClick={() => setStep((s) => Math.max(0, s - 1))} disabled={step === 0}>
              {t('app.back')}
            </Button>

            {step < 3 ? (
              <Button onClick={() => setStep((s) => s + 1)} disabled={step === 1 && !accountValid}>
                {t('app.next')}
              </Button>
            ) : (
              <Button onClick={() => void finish()} loading={busy} disabled={!accountValid}>
                {t('app.finish')}
              </Button>
            )}
          </Group>
        </Card>
      </Stack>
    </Container>
  );
}
