import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Checkbox, Group, Modal, NumberInput, Select, Stack, Text } from '@mantine/core';

import { api, ApiError, type Page } from '../../lib/api';

/**
 * Owner, review interval and whether the page must be acknowledged.
 *
 * The owner is a **username**, not free text, because everything downstream needs a user id: the
 * dashboard asks "what do I own", the review scan asks "who do I tell". A picker writes the right
 * thing so nobody has to know that.
 *
 * Setting an interval restarts the clock from today, which is what "review this every 90 days"
 * means when somebody types it in. Confirming a review — a different action, on the page banner —
 * is the only other thing that moves the date.
 */
export function LifecyclePanel({ page, opened, onClose }: { page: Page; opened: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();

  const [owner, setOwner] = useState<string | null>(null);
  const [interval, setInterval] = useState<number | string>('');
  const [requiresAcknowledgment, setRequiresAcknowledgment] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  // Only while the dialog is open: a picker on every page view would be a request per navigation
  // for a list most readers never see.
  const people = useQuery({ queryKey: ['users', 'pickable'], queryFn: api.pickableUsers, enabled: opened });

  useEffect(() => {
    if (opened) {
      setOwner(page.owner ?? null);
      setInterval(page.reviewIntervalDays ?? '');
      setRequiresAcknowledgment(page.requiresAcknowledgment);
      setFailure(null);
    }
  }, [opened, page]);

  const save = useMutation({
    mutationFn: () =>
      api.setLifecycle({
        path: page.path,
        // An empty string clears the owner; null would mean "leave it alone".
        owner: owner ?? '',
        reviewIntervalDays: typeof interval === 'number' ? interval : null,
        requiresAcknowledgment,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['page', page.path] });
      await queryClient.invalidateQueries({ queryKey: ['dashboard'] });
      await queryClient.invalidateQueries({ queryKey: ['acknowledgments', 'mine'] });
      onClose();
    },
    onError: (error) =>
      setFailure(error instanceof ApiError ? t(`error.${error.code}`, { defaultValue: error.detail }) : String(error)),
  });

  // The stored owner may name somebody who has left. Keeping it in the list means opening this
  // dialog does not silently erase what a human typed.
  const options = (people.data ?? []).map((person) => ({
    value: person.userName,
    label: `${person.displayName} (${person.userName})`,
  }));

  if (owner && !options.some((option) => option.value === owner)) {
    options.unshift({ value: owner, label: t('lifecycle.unknownOwner', { owner }) });
  }

  return (
    <Modal opened={opened} onClose={onClose} title={t('lifecycle.panelTitle')}>
      <Stack gap="md">
        <Select
          label={t('lifecycle.owner')}
          description={t('lifecycle.ownerHint')}
          data={options}
          value={owner}
          onChange={setOwner}
          searchable
          clearable
          nothingFoundMessage={t('lifecycle.noMatch')}
        />

        <NumberInput
          label={t('lifecycle.interval')}
          description={t('lifecycle.intervalHint')}
          value={interval}
          onChange={setInterval}
          min={1}
          max={3650}
          allowDecimal={false}
        />

        <Checkbox
          label={t('lifecycle.requiresAcknowledgment')}
          description={t('lifecycle.requiresAcknowledgmentHint')}
          checked={requiresAcknowledgment}
          onChange={(event) => setRequiresAcknowledgment(event.currentTarget.checked)}
        />

        {page.nextReviewDate && (
          <Text size="sm" c="dimmed">
            {t('lifecycle.currentlyDue', { date: new Date(page.nextReviewDate).toLocaleDateString() })}
          </Text>
        )}

        {failure && <Alert color="red">{failure}</Alert>}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button onClick={() => save.mutate()} loading={save.isPending}>
            {t('common.save')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
