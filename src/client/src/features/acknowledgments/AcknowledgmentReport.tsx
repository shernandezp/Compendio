import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { Badge, Button, Group, Loader, Modal, Progress, Stack, Table, Text } from '@mantine/core';

import { api } from '../../lib/api';
import { formatRelative } from '../../lib/format';

/**
 * Who has confirmed reading a page, and who has not.
 *
 * "Required" is everyone who can read the page, answered by the permission evaluator on the server
 * rather than guessed from a role — a reader restricted out of the folder owes nothing. Reading this
 * needs `manage` on the folder: a list of who has and has not done something is different
 * information from the page itself.
 */
export function AcknowledgmentReport({
  path,
  opened,
  onClose,
}: {
  path: string;
  opened: boolean;
  onClose: () => void;
}) {
  const { t, i18n } = useTranslation();

  const report = useQuery({
    queryKey: ['acknowledgments', 'page', path],
    queryFn: () => api.acknowledgmentReport(path),
    enabled: opened,
  });

  const data = report.data;
  const percent = data && data.requiredCount > 0 ? (data.acknowledgedCount / data.requiredCount) * 100 : 0;

  return (
    <Modal opened={opened} onClose={onClose} title={t('acknowledgment.report')} size="lg">
      {report.isPending ? (
        <Loader />
      ) : !data ? (
        <Text c="dimmed">—</Text>
      ) : (
        <Stack gap="md">
          <Group justify="space-between">
            <Text size="sm">
              {t('acknowledgment.progress', {
                done: data.acknowledgedCount,
                total: data.requiredCount,
              })}
            </Text>

            <Button
              component="a"
              href={`/api/v1/acknowledgments/report.csv?path=${encodeURIComponent(path)}`}
              variant="default"
              size="xs"
            >
              {t('common.exportCsv')}
            </Button>
          </Group>

          <Progress value={percent} />

          {/* The version is the point: an acknowledgment records one, and the report says which. */}
          <Text size="xs" c="dimmed">
            {t('acknowledgment.version', { sequence: data.currentVersionSequence })}
          </Text>

          <Table.ScrollContainer minWidth={420}>
            <Table striped>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('acknowledgment.person')}</Table.Th>
                  <Table.Th>{t('acknowledgment.status')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {data.people.map((person) => (
                  <Table.Tr key={person.userId}>
                    <Table.Td>{person.displayName}</Table.Td>
                    <Table.Td>
                      {person.hasAcknowledged && person.acknowledgedAt ? (
                        <Badge color="green" size="sm">
                          {formatRelative(person.acknowledgedAt, i18n.language)}
                        </Badge>
                      ) : (
                        <Badge color="gray" size="sm">
                          {t('acknowledgment.outstanding')}
                        </Badge>
                      )}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Stack>
      )}
    </Modal>
  );
}
