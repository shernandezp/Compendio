import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Card,
  Checkbox,
  Divider,
  Group,
  Loader,
  NumberInput,
  PasswordInput,
  Progress,
  Stack,
  TagsInput,
  Text,
  TextInput,
  Title,
} from '@mantine/core';

import { api } from '../../lib/api';

/**
 * Where an administrator turns AI on, and off.
 *
 * A base URL, a model and an optional key — the whole configuration, because one OpenAI-compatible
 * integration covers Ollama, Groq, OpenAI, Azure OpenAI, LM Studio and vLLM. The key is write-only
 * from here: the server reports whether one is stored and never returns it, so it does not make a
 * round trip through a browser every time somebody opens this screen.
 */
export function AiSettingsPanel() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const settings = useQuery({ queryKey: ['admin', 'ai'], queryFn: api.aiSettings });

  const [baseUrl, setBaseUrl] = useState('');
  const [model, setModel] = useState('');
  const [apiKey, setApiKey] = useState('');
  const [allowedSpaces, setAllowedSpaces] = useState<string[]>([]);
  const [disabledFeatures, setDisabledFeatures] = useState<string[]>([]);
  const [dailyPerUser, setDailyPerUser] = useState(0);
  const [dailyPerInstance, setDailyPerInstance] = useState(0);

  useEffect(() => {
    if (settings.data) {
      setBaseUrl(settings.data.baseUrl);
      setModel(settings.data.model);
      setAllowedSpaces(settings.data.allowedSpaces);
      setDisabledFeatures(settings.data.disabledFeatures);
      setDailyPerUser(settings.data.dailyPerUser);
      setDailyPerInstance(settings.data.dailyPerInstance);
    }
  }, [settings.data]);

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: ['admin', 'ai'] });
    // The whole UI renders AI from this, so it has to be refetched or the buttons lag the setting.
    void queryClient.invalidateQueries({ queryKey: ['ai', 'status'] });
  };

  const save = useMutation({
    // An untouched key field sends nothing, which the server reads as "leave it alone".
    mutationFn: () => api.saveAiSettings({
      baseUrl,
      model,
      apiKey: apiKey.length > 0 ? apiKey : undefined,
      allowedSpaces,
      disabledFeatures,
      dailyPerUser,
      dailyPerInstance,
    }),
    onSuccess: () => {
      setApiKey('');
      invalidate();
    },
  });

  const toggleFeature = (feature: string, on: boolean) =>
    setDisabledFeatures((current) =>
      on ? current.filter((f) => f !== feature) : [...new Set([...current, feature])]);

  const clear = useMutation({ mutationFn: api.clearAiSettings, onSuccess: invalidate });
  const test = useMutation({ mutationFn: api.testAiConnection });

  if (settings.isPending) {
    return <Loader />;
  }

  return (
    <Card withBorder padding="md">
      <Stack gap="md">
        <Title order={4}>{t('admin.ai.title')}</Title>

        <Text size="sm" c="dimmed">
          {t('admin.ai.intro')}
        </Text>

        <TextInput
          label={t('admin.ai.baseUrl')}
          description={t('admin.ai.baseUrlHint')}
          placeholder="http://localhost:11434/v1"
          value={baseUrl}
          onChange={(event) => setBaseUrl(event.currentTarget.value)}
        />

        <TextInput
          label={t('admin.ai.model')}
          description={t('admin.ai.modelHint')}
          placeholder="llama-3.3-70b-versatile"
          value={model}
          onChange={(event) => setModel(event.currentTarget.value)}
        />

        <PasswordInput
          label={t('admin.ai.apiKey')}
          description={
            settings.data?.hasApiKey ? t('admin.ai.apiKeyStored') : t('admin.ai.apiKeyOptional')
          }
          value={apiKey}
          onChange={(event) => setApiKey(event.currentTarget.value)}
        />

        {/* Stated plainly, because for this audience it is the deciding question. */}
        <Alert variant="light" color="blue">
          {settings.data?.enabled
            ? t('admin.ai.privacyOn', { endpoint: settings.data.endpointLabel })
            : t('admin.ai.privacyOff')}
        </Alert>

        <Divider label={t('admin.ai.limitsTitle')} labelPosition="left" />

        {/* The cost control. Named as such rather than as a "rate limit", because the reason it
            exists is a bill on somebody's card and every other limit in the product is about load. */}
        <Text size="sm" c="dimmed">
          {t('admin.ai.limitsIntro')}
        </Text>

        <Group grow align="flex-start">
          <NumberInput
            label={t('admin.ai.dailyPerUser')}
            description={t('admin.ai.dailyPerUserHint')}
            value={dailyPerUser}
            onChange={(value) => setDailyPerUser(typeof value === 'number' ? value : 0)}
            min={0}
            step={10}
            allowNegative={false}
          />
          <NumberInput
            label={t('admin.ai.dailyPerInstance')}
            description={t('admin.ai.dailyPerInstanceHint')}
            value={dailyPerInstance}
            onChange={(value) => setDailyPerInstance(typeof value === 'number' ? value : 0)}
            min={0}
            step={50}
            allowNegative={false}
          />
        </Group>

        {/* What has actually been spent, so a cap is a decision rather than a guess. */}
        {settings.data?.enabled && (
          <Stack gap={6}>
            <Text size="sm">
              {t('admin.ai.usedInLastDay', { used: settings.data.instanceUsage.used })}
            </Text>

            {settings.data.instanceUsage.limit > 0 && (
              <Progress
                value={(settings.data.instanceUsage.used / settings.data.instanceUsage.limit) * 100}
                size="sm"
              />
            )}

            {settings.data.topSpenders.length > 0 && (
              <Text size="xs" c="dimmed">
                {t('admin.ai.topSpenders')}:{' '}
                {settings.data.topSpenders
                  .map((spender) => `${spender.displayName} (${spender.requests})`)
                  .join(', ')}
              </Text>
            )}
          </Stack>
        )}

        <Divider label={t('admin.ai.scopeTitle')} labelPosition="left" />

        <TagsInput
          label={t('admin.ai.allowedSpaces')}
          description={t('admin.ai.allowedSpacesHint')}
          placeholder={t('admin.ai.allowedSpacesPlaceholder')}
          value={allowedSpaces}
          onChange={setAllowedSpaces}
          clearable
        />

        <Stack gap={6}>
          <Text size="sm" fw={500}>
            {t('admin.ai.features')}
          </Text>
          <Text size="xs" c="dimmed">
            {t('admin.ai.featuresHint')}
          </Text>

          {/* Switched off individually rather than only all-or-nothing: an organization that wants
              rewriting help but not question answering over its HR folder is a real position, and
              without this the only way to take it was to turn AI off entirely. */}
          <Group gap="md" mt={4}>
            {(settings.data?.availableFeatures ?? []).map((feature) => (
              <Checkbox
                key={feature}
                label={t(`ai.feature.${feature}`, { defaultValue: feature })}
                checked={!disabledFeatures.includes(feature)}
                onChange={(event) => toggleFeature(feature, event.currentTarget.checked)}
              />
            ))}
          </Group>
        </Stack>

        {test.data && (
          <Alert color={test.data.ok ? 'green' : 'red'}>
            {test.data.ok
              ? t('admin.ai.testOk', { model: test.data.model, reply: test.data.detail })
              : t('admin.ai.testFailed', { detail: test.data.detail })}
          </Alert>
        )}

        <Group>
          <Button onClick={() => save.mutate()} loading={save.isPending}>
            {t('common.save')}
          </Button>
          <Button variant="default" onClick={() => test.mutate()} loading={test.isPending}>
            {t('admin.ai.test')}
          </Button>
          {settings.data?.enabled && (
            <Button variant="subtle" color="red" onClick={() => clear.mutate()} loading={clear.isPending}>
              {t('admin.ai.disable')}
            </Button>
          )}
        </Group>
      </Stack>
    </Card>
  );
}

/**
 * The git mirror's state, read-only.
 *
 * Configured in `appsettings.json` or the environment rather than here, because it is a deployment
 * decision that belongs with the rest of them — and because Compendio stores no git credential of
 * its own, so there is nothing to type in. What this screen adds is the answer to "is it working".
 */
export function GitMirrorPanel() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const status = useQuery({ queryKey: ['admin', 'git-mirror'], queryFn: api.gitMirror });

  const push = useMutation({
    mutationFn: api.pushGitMirror,
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['admin', 'git-mirror'] }),
  });

  if (status.isPending) {
    return <Loader />;
  }

  const data = status.data;

  return (
    <Card withBorder padding="md">
      <Stack gap="sm">
        <Title order={4}>{t('admin.git.title')}</Title>

        {!data?.enabled ? (
          <Text size="sm" c="dimmed">
            {t('admin.git.disabled')}
          </Text>
        ) : !data.gitAvailable ? (
          // Reported, never fatal: git missing must leave everything else working.
          <Alert color="yellow">{t('admin.git.noGit')}</Alert>
        ) : (
          <Stack gap="xs">
            <Text size="sm">{t('admin.git.branch', { branch: data.branch })}</Text>

            {data.lastError ? (
              <Alert color="red">{t('admin.git.lastError', { detail: data.lastError })}</Alert>
            ) : (
              <Text size="sm" c="dimmed">
                {data.lastSuccessAt
                  ? t('admin.git.lastSuccess', { when: data.lastSuccessAt })
                  : t('admin.git.neverPushed')}
              </Text>
            )}

            <Group>
              <Button size="xs" onClick={() => push.mutate()} loading={push.isPending}>
                {t('admin.git.pushNow')}
              </Button>
            </Group>
          </Stack>
        )}
      </Stack>
    </Card>
  );
}
