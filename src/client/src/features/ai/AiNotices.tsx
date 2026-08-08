import { useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Alert, Progress, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';

import { ApiError, type AiBudget } from '../../lib/api';

/**
 * Turns the server's `resetsAt` timestamp into a sentence in the reader's locale and timezone.
 *
 * One helper because both failure surfaces need it and neither should re-derive the format: the
 * inline alert and the notification have to say the same thing about the same refusal.
 */
export function useAiResetLabel() {
  const { t, i18n } = useTranslation();

  return useCallback(
    (iso: string) =>
      t('ai.quotaResetsAt', {
        when: new Date(iso).toLocaleString(i18n.language, {
          hour: 'numeric',
          minute: '2-digit',
          day: 'numeric',
          month: 'short',
        }),
      }),
    [t, i18n.language],
  );
}

/** True for the two codes that mean "an allowance ran out" rather than "something broke". */
const isQuota = (error: Error) =>
  error instanceof ApiError &&
  (error.code === 'ai.quota_exceeded' || error.code === 'ai.quota_exceeded_instance');

/**
 * Reports an AI failure where there is no room for an alert — a toolbar, a row of buttons.
 *
 * @remarks
 * `AiMenu` lives inside a `wrap="nowrap"` button group on both the read view and the editor, so an
 * inline `Alert` there would either squeeze the buttons beside it or wrap the row. Out-of-band
 * feedback goes through notifications everywhere else in the product; this is the same choice.
 *
 * A quota refusal does not auto-close. It is the one message here that asks the reader to do
 * something afterwards — wait, or go and ask an administrator — and four seconds is not enough to
 * read a sentence and decide.
 */
export function notifyAiFailure(error: Error, fallback: string, resetsAtLabel?: (iso: string) => string) {
  const quota = isQuota(error);
  const detail = error instanceof ApiError ? error.detail || fallback : error.message || fallback;

  // The server deliberately leaves "when" out of the sentence — a rolling window makes any
  // server-side duration read either fine or absurd — and sends it as a timestamp instead. Both
  // failure surfaces append it here, formatted in the reader's own locale and timezone.
  const resetsAt = error instanceof ApiError && typeof error.extensions.resetsAt === 'string'
    ? error.extensions.resetsAt
    : null;

  notifications.show({
    color: quota ? 'yellow' : 'red',
    title: error instanceof ApiError ? error.title || undefined : undefined,
    message: quota && resetsAt && resetsAtLabel ? `${detail} ${resetsAtLabel(resetsAt)}` : detail,
    autoClose: quota ? false : 8000,
    withCloseButton: true,
  });
}

/**
 * An AI failure, inline, where the surface has room to keep it on screen.
 *
 * @remarks
 * Used by the three AI surfaces that own their space — the translate dialog, the draft dialog and
 * the Ask page. `AiMenu` is a single control in a toolbar and uses {@link notifyAiFailure} instead;
 * the wording comes from the same server detail either way, so the two cannot drift into saying
 * different things about the same refusal.
 *
 * The quota refusal in particular has to read the same wherever it lands: the person seeing it needs
 * to know it is temporary, that nothing was changed, and roughly how long — more than a red box
 * saying "429".
 *
 * The server has already localized `detail` in the caller's language, so this renders it rather than
 * re-deriving a message from the code. The extra line below it comes from the machine-readable
 * extensions, not from parsing that sentence back apart.
 */
export function AiFailure({ error, onClose }: { error: Error | null; onClose: () => void }) {
  const { t } = useTranslation();
  const resetLabel = useAiResetLabel();

  if (!error) {
    return null;
  }

  if (!(error instanceof ApiError)) {
    return (
      <Alert color="red" mt="sm" withCloseButton onClose={onClose}>
        {String(error.message)}
      </Alert>
    );
  }

  const quota = isQuota(error);
  const resetsAt = typeof error.extensions.resetsAt === 'string' ? error.extensions.resetsAt : null;

  return (
    // Amber, not red. Running out of an allowance an administrator set is the system working as
    // configured; colouring it like a crash tells the user something is broken when nothing is.
    <Alert
      color={quota ? 'yellow' : 'red'}
      title={error.title || undefined}
      mt="sm"
      withCloseButton
      onClose={onClose}
    >
      <Stack gap={4}>
        <Text size="sm">{error.detail || t('app.error.generic')}</Text>

        {quota && resetsAt && (
          <Text size="xs" c="dimmed">
            {resetLabel(resetsAt)}
          </Text>
        )}
      </Stack>
    </Alert>
  );
}

/**
 * How much of the allowance is left, shown only once it is nearly gone.
 *
 * @remarks
 * A counter on screen at all times would read as a warning and make an optional feature feel
 * rationed. The useful moment is the last few requests, when knowing changes whether somebody spends
 * one on a typo.
 */
export function AiBudgetNotice({ budget }: { budget: AiBudget | undefined }) {
  const { t } = useTranslation();

  if (!budget || budget.limit <= 0 || budget.remaining === null) {
    return null;
  }

  return (
    <Stack gap={4} px="xs" py={6}>
      <Text size="xs" c="dimmed">
        {t('ai.budgetRemaining', { remaining: budget.remaining, limit: budget.limit })}
      </Text>
      <Progress
        // Clamped: two requests that race the same last slot can push `used` past `limit`, and a
        // bar drawn past its own end looks like a rendering bug rather than a rounding one.
        value={Math.min(100, (budget.used / budget.limit) * 100)}
        color={budget.remaining === 0 ? 'red' : 'yellow'}
        size="xs"
        aria-label={t('ai.budgetRemaining', { remaining: budget.remaining, limit: budget.limit })}
      />
    </Stack>
  );
}
