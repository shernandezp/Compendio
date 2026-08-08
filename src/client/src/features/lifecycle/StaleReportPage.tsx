import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import {
  Anchor,
  Badge,
  Button,
  Group,
  Loader,
  Pagination,
  Stack,
  Table,
  Text,
  Title,
  VisuallyHidden,
} from '@mantine/core';

import { api, encodePath } from '../../lib/api';
import { formatRelative } from '../../lib/format';
import { aiFeatures, useAiStatus } from '../ai/useAi';
import { AiFreshnessButton } from '../ai/AiFreshnessButton';

/** The sentinel the API uses for "no reachable owner". */
const UNASSIGNED = '-';

/**
 * Every page past its review date that the reader can see.
 *
 * Permission-filtered on the server, totals included — "12 overdue" means twelve *you* can see. The
 * unassigned filter is the interesting one: it selects pages nobody reachable owns, including pages
 * naming somebody who has left, which is exactly the case this report exists to surface.
 */
export function StaleReportPage() {
  const { t, i18n } = useTranslation();
  const [page, setPage] = useState(1);
  const [unassignedOnly, setUnassignedOnly] = useState(false);

  /**
   * Asked once for the whole table, not once per row.
   *
   * The answer is the same for every row, and it decides whether a column exists at all — so fifty
   * rows each subscribing to it in order to render fifty empty cells is both the slower and the
   * uglier way to get there.
   */
  const ai = useAiStatus();
  const canCheckFreshness = ai.has(aiFeatures.freshness);

  const report = useQuery({
    queryKey: ['stale', page, unassignedOnly],
    queryFn: () => api.staleReport(page, 50, unassignedOnly ? UNASSIGNED : undefined),
  });

  if (report.isPending) {
    return <Loader />;
  }

  const data = report.data;
  const items = data?.items ?? [];
  const pages = Math.max(1, Math.ceil((data?.totalCount ?? 0) / (data?.pageSize ?? 50)));

  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <Title order={2}>{t('stale.title')}</Title>

        <Group gap="xs">
          <Button
            variant={unassignedOnly ? 'filled' : 'light'}
            size="xs"
            onClick={() => {
              setUnassignedOnly((value) => !value);
              setPage(1);
            }}
          >
            {t('stale.unassignedOnly')}
          </Button>

          <Button
            component="a"
            href={`/api/v1/lifecycle/stale.csv${unassignedOnly ? `?owner=${UNASSIGNED}` : ''}`}
            variant="default"
            size="xs"
          >
            {t('common.exportCsv')}
          </Button>
        </Group>
      </Group>

      <Text c="dimmed" size="sm">
        {t('stale.count', { count: data?.totalCount ?? 0 })}
      </Text>

      {items.length === 0 ? (
        <Text c="dimmed">{t('stale.empty')}</Text>
      ) : (
        <Table.ScrollContainer minWidth={600}>
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('stale.page')}</Table.Th>
                <Table.Th>{t('stale.owner')}</Table.Th>
                <Table.Th>{t('stale.due')}</Table.Th>
                <Table.Th>{t('stale.overdue')}</Table.Th>
                {/* Named for screen readers, blank on screen: a heading wide enough to read would
                    be wider than the icon column it titles. The name is the *column's* — "freshness
                    check" — not the button's, which says what pressing it does; an `aria-label` here
                    would give the header and its buttons the same accessible name. */}
                {canCheckFreshness && (
                  <Table.Th w={48}>
                    <VisuallyHidden>{t('stale.freshnessColumn')}</VisuallyHidden>
                  </Table.Th>
                )}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {items.map((row) => (
                <Table.Tr key={row.path}>
                  <Table.Td>
                    <Anchor component={Link} to={`/p/${encodePath(row.path)}`}>
                      {row.title}
                    </Anchor>
                  </Table.Td>
                  <Table.Td>
                    {row.unassigned ? (
                      // The owner string is still shown when there is one: it is what a human typed,
                      // and "someone.who.left" tells an administrator far more than "unassigned".
                      <Group gap="xs">
                        <Badge color="gray" size="sm">
                          {t('stale.unassigned')}
                        </Badge>
                        {row.owner && <Text size="xs" c="dimmed">{row.owner}</Text>}
                      </Group>
                    ) : (
                      <Text size="sm">{row.ownerDisplayName ?? row.owner}</Text>
                    )}
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm" c="dimmed">
                      {row.nextReviewDate ? formatRelative(row.nextReviewDate, i18n.language) : '—'}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <Badge color={(row.daysOverdue ?? 0) > 90 ? 'red' : 'yellow'} size="sm">
                      {t('stale.days', { count: row.daysOverdue ?? 0 })}
                    </Badge>
                  </Table.Td>
                  {/* "Overdue for review" and "actually out of date" are different questions, and
                      this is the one the report could not answer until now. */}
                  {canCheckFreshness && (
                    <Table.Td>
                      <AiFreshnessButton path={row.path} endpointLabel={ai.endpointLabel} />
                    </Table.Td>
                  )}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}

      {pages > 1 && <Pagination value={page} onChange={setPage} total={pages} />}
    </Stack>
  );
}
