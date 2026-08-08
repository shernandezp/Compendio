import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { ActionIcon, Indicator, Tooltip } from '@mantine/core';
import { IconBell } from '@tabler/icons-react';

import { api } from '../../lib/api';

/**
 * The unread badge in the header.
 *
 * The count comes from the same permission re-check as the list, so it can never be higher than the
 * number of notifications the person can actually open — a badge that counted rows the list then
 * dropped would be a count of pages they cannot see.
 *
 * Polled rather than pushed: there is no websocket in this product, and a minute of staleness on an
 * inbox that exists because there is no email is an easy trade.
 */
export function NotificationBell() {
  const { t } = useTranslation();

  const count = useQuery({
    queryKey: ['notifications', 'count'],
    queryFn: api.notificationCount,
    refetchInterval: 60_000,
    retry: false,
  });

  const unread = count.data?.count ?? 0;

  return (
    <Tooltip label={t('nav.notifications')}>
      <Indicator label={unread > 99 ? '99+' : unread} size={16} disabled={unread === 0} offset={4}>
        <ActionIcon component={Link} to="/notifications" variant="subtle" aria-label={t('nav.notifications')}>
          <IconBell size={18} />
        </ActionIcon>
      </Indicator>
    </Tooltip>
  );
}
