import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { Badge, Group, Loader, Stack, Text, Title } from '@mantine/core';

import { api } from '../../lib/api';

/**
 * Tag browsing.
 *
 * The counts come from the API already filtered — recomputed per user rather than cached globally,
 * because a shared count tells a reader how many pages sit behind a folder they cannot open. This
 * screen therefore does no arithmetic of its own; it renders what it was given.
 */
export function TagBrowse() {
  const { t } = useTranslation();
  const tags = useQuery({ queryKey: ['tags'], queryFn: api.tags });

  if (tags.isPending) {
    return <Loader />;
  }

  const data = tags.data ?? [];
  const busiest = Math.max(1, ...data.map((tag) => tag.count));

  return (
    <Stack gap="lg">
      <Title order={2}>{t('nav.tags')}</Title>

      {data.length === 0 ? (
        <Text c="dimmed">—</Text>
      ) : (
        <Group gap="sm">
          {data.map((tag) => (
            <Badge
              key={tag.tag}
              component={Link}
              to={`/search?q=${encodeURIComponent(`tag:${tag.tag}`)}`}
              variant="light"
              // Size carries the count, so the shape of the wiki is visible at a glance.
              size={tag.count > busiest * 0.66 ? 'lg' : tag.count > busiest * 0.33 ? 'md' : 'sm'}
              style={{ cursor: 'pointer', textDecoration: 'none' }}
            >
              {tag.tag} · {tag.count}
            </Badge>
          ))}
        </Group>
      )}
    </Stack>
  );
}
