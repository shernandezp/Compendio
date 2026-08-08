import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { Alert, Anchor, Badge, Card, Group, Loader, Pagination, Stack, Text, TextInput, Title } from '@mantine/core';
import { IconSearch } from '@tabler/icons-react';

import { api, encodePath } from '../../lib/api';
import { formatRelative } from '../../lib/format';

export function SearchPage() {
  const { t, i18n } = useTranslation();
  const [params, setParams] = useSearchParams();

  const query = params.get('q') ?? '';
  const page = Number(params.get('page') ?? '1');
  const [draft, setDraft] = useState(query);

  useEffect(() => setDraft(query), [query]);

  const results = useQuery({
    queryKey: ['search', query, page],
    queryFn: () => api.search(query, page),
    enabled: query.trim().length > 0,
  });

  const recent = useQuery({
    queryKey: ['recent'],
    queryFn: () => api.recent(10),
    enabled: query.trim().length === 0,
  });

  function submit(event: React.FormEvent) {
    event.preventDefault();
    setParams({ q: draft, page: '1' });
  }

  return (
    <Stack gap="lg">
      <form onSubmit={submit}>
        <TextInput
          size="md"
          value={draft}
          onChange={(event) => setDraft(event.currentTarget.value)}
          placeholder={t('search.placeholder')}
          leftSection={<IconSearch size={18} />}
          aria-label={t('app.search')}
        />
      </form>

      <Text size="xs" c="dimmed">
        {t('search.filters')}
      </Text>

      {query.trim().length === 0 ? (
        <Stack gap="sm">
          <Title order={3}>{t('nav.recent')}</Title>
          {recent.isPending ? (
            <Loader size="sm" />
          ) : (
            (recent.data ?? []).map((hit) => (
              <Card key={hit.path} withBorder padding="sm">
                <Anchor component={Link} to={`/p/${encodePath(hit.path)}`} fw={600}>
                  {hit.title}
                </Anchor>
                <Text size="xs" c="dimmed">
                  {hit.path} · {formatRelative(hit.updatedAt, i18n.language)}
                </Text>
              </Card>
            ))
          )}
        </Stack>
      ) : results.isPending ? (
        <Loader />
      ) : results.data && results.data.totalCount > 0 ? (
        <Stack gap="sm">
          {/* The count uses the same permission predicate as the results: "12 results" means twelve
              results you can see. */}
          <Text size="sm" c="dimmed">
            {t('search.results', { count: results.data.totalCount })}
          </Text>

          {results.data.items.map((hit) => (
            <Card key={hit.path} withBorder padding="sm">
              <Group justify="space-between" wrap="nowrap" align="flex-start">
                <div>
                  <Anchor component={Link} to={`/p/${encodePath(hit.path)}`} fw={600}>
                    {hit.title}
                  </Anchor>
                  <Text size="xs" c="dimmed">
                    {hit.path}
                  </Text>
                  {/* Escaped server-side; the <mark> tags are the only markup in it. */}
                  <Text size="sm" mt={4} component="div" dangerouslySetInnerHTML={{ __html: hit.excerpt }} />
                  <Group gap={4} mt={6}>
                    {hit.tags.map((tag) => (
                      <Badge key={tag} size="xs" variant="light">
                        {tag}
                      </Badge>
                    ))}
                  </Group>
                </div>

                <Stack gap={2} align="flex-end">
                  {hit.lang && hit.lang !== i18n.language && (
                    <Badge size="xs" variant="outline">
                      {t('search.inLanguage', { language: hit.lang })}
                    </Badge>
                  )}
                  <Text size="xs" c="dimmed" style={{ whiteSpace: 'nowrap' }}>
                    {formatRelative(hit.updatedAt, i18n.language)}
                  </Text>
                </Stack>
              </Group>
            </Card>
          ))}

          {results.data.totalCount > results.data.pageSize && (
            <Pagination
              total={Math.ceil(results.data.totalCount / results.data.pageSize)}
              value={page}
              onChange={(next) => setParams({ q: query, page: String(next) })}
            />
          )}
        </Stack>
      ) : (
        <Alert variant="light" title={t('search.noResults', { query })}>
          {t('search.noResultsHint')}
        </Alert>
      )}
    </Stack>
  );
}
