import { useTranslation } from 'react-i18next';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Group, Stack, Text } from '@mantine/core';

import { api, type Page } from '../../lib/api';

/**
 * The banner on a stale page.
 *
 * One of the three places staleness is surfaced, and the one nobody can miss: it sits on the page
 * itself, visible to anyone who can read it. Only somebody who can write the page sees the button —
 * confirming a review is a change to the page's metadata.
 */
export function StaleBanner({ page }: { page: Page }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const confirm = useMutation({
    mutationFn: () => api.confirmReviewed(page.path),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['page', page.path] });
      void queryClient.invalidateQueries({ queryKey: ['dashboard'] });
      void queryClient.invalidateQueries({ queryKey: ['notifications'] });
    },
  });

  // Read straight off the page the screen already fetched. Asking the server a second time would be
  // a request per page view for a flag it has already sent.
  if (!page.isStale) {
    return null;
  }

  const canWrite = page.level === 'Write' || page.level === 'Manage';

  return (
    <Alert color="yellow" title={t('lifecycle.staleTitle')}>
      <Group justify="space-between" wrap="wrap">
        <Text size="sm">{t('lifecycle.staleBody', { owner: page.owner ?? t('lifecycle.nobody') })}</Text>

        {canWrite && (
          <Button size="xs" onClick={() => confirm.mutate()} loading={confirm.isPending}>
            {t('lifecycle.confirmReviewed')}
          </Button>
        )}
      </Group>
    </Alert>
  );
}

/**
 * The banner on a page that requires acknowledgment.
 *
 * Acknowledging is an explicit action, never inferred from the page having been opened. An
 * acknowledgment derived from a page view is worthless to the compliance case the feature exists
 * for, and worse than worthless if anybody relies on it.
 */
export function AcknowledgmentBanner({ path }: { path: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const outstanding = useQuery({ queryKey: ['acknowledgments', 'mine'], queryFn: api.myAcknowledgments });

  const acknowledge = useMutation({
    mutationFn: () => api.acknowledge(path),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['acknowledgments', 'mine'] });
      void queryClient.invalidateQueries({ queryKey: ['dashboard'] });
      void queryClient.invalidateQueries({ queryKey: ['notifications'] });
    },
  });

  const owed = outstanding.data?.find((task) => task.path === path);

  if (!owed) {
    return null;
  }

  return (
    <Alert color={owed.overdue ? 'red' : 'blue'} title={t('acknowledgment.required')}>
      <Stack gap="sm">
        <Text size="sm">{t('acknowledgment.body')}</Text>

        <Group>
          <Button size="xs" onClick={() => acknowledge.mutate()} loading={acknowledge.isPending}>
            {t('acknowledgment.confirm')}
          </Button>
        </Group>
      </Stack>
    </Alert>
  );
}

/**
 * The badge on a machine translation nobody has reviewed.
 *
 * Driven by `machineTranslated` in the page's own front matter, so it survives export, a git mirror
 * and somebody copying the file. A wrong Spanish HR policy is worse than no Spanish HR policy, and
 * the badge is the difference between the two.
 */
export function MachineTranslationBanner({ content }: { content?: string }) {
  const { t } = useTranslation();

  if (!content || !/^machineTranslated:\s*true\s*$/m.test(content)) {
    return null;
  }

  return (
    <Alert color="orange" title={t('ai.unreviewedTitle')}>
      <Text size="sm">{t('ai.unreviewedBody')}</Text>
    </Alert>
  );
}
