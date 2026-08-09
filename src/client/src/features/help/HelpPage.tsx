import { Link, Navigate, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { Alert, Badge, Card, Divider, Grid, Group, Loader, Stack, Text, Title } from '@mantine/core';

import { api } from '../../lib/api';

/**
 * The built-in guide.
 *
 * Not wiki content: seeding these as pages would put them in the customer's content folder, where
 * they can be edited into something that no longer describes the product. The trade-off is that
 * they are not searchable, which is why the help button is in the header rather than only here.
 */
export function HelpPage() {
  const { t } = useTranslation();
  const { slug } = useParams();

  const topics = useQuery({ queryKey: ['help', 'topics'], queryFn: api.helpTopics });

  if (topics.isPending) {
    return <Loader />;
  }

  const list = topics.data ?? [];

  // /help lands on the first topic rather than an index page: a table of contents whose only
  // content is a table of contents is a wasted screen.
  if (!slug && list.length > 0) {
    return <Navigate to={`/help/${list[0]!.slug}`} replace />;
  }

  const forEveryone = list.filter((topic) => !topic.isAdmin);
  const forAdmins = list.filter((topic) => topic.isAdmin);

  return (
    <Grid>
      <Grid.Col span={{ base: 12, sm: 4, md: 3 }}>
        <Stack gap="xs">
          <Title order={4}>{t('help.title')}</Title>

          {forEveryone.map((topic) => (
            <TopicLink key={topic.slug} slug={topic.slug} title={topic.title} active={topic.slug === slug} />
          ))}

          {forAdmins.length > 0 && (
            <>
              <Divider my="xs" label={t('help.forAdministrators')} labelPosition="left" />
              {forAdmins.map((topic) => (
                <TopicLink key={topic.slug} slug={topic.slug} title={topic.title} active={topic.slug === slug} />
              ))}
            </>
          )}
        </Stack>
      </Grid.Col>

      <Grid.Col span={{ base: 12, sm: 8, md: 9 }}>
        {slug ? <HelpTopicContent slug={slug} /> : <Alert variant="light">{t('help.empty')}</Alert>}
      </Grid.Col>
    </Grid>
  );
}

function TopicLink({ slug, title, active }: { slug: string; title: string; active: boolean }) {
  return (
    <Text
      component={Link}
      to={`/help/${slug}`}
      size="sm"
      fw={active ? 700 : 400}
      style={{ textDecoration: 'none' }}
    >
      {title}
    </Text>
  );
}

function HelpTopicContent({ slug }: { slug: string }) {
  const { t } = useTranslation();

  const page = useQuery({ queryKey: ['help', 'page', slug], queryFn: () => api.helpPage(slug) });

  if (page.isPending) {
    return <Loader />;
  }

  if (page.isError || !page.data) {
    return <Alert color="red" title={t('app.error.title')}>{t('help.notFound')}</Alert>;
  }

  return (
    <Stack gap="md">
      <Group gap="sm" align="center">
        <Title order={2}>{page.data.title}</Title>
        {page.data.isAdmin && (
          <Badge variant="light" size="sm">
            {t('help.forAdministrators')}
          </Badge>
        )}
      </Group>

      {/* A half-finished translation shows the translated topics in the reader's language and the
          rest in English, flagged — rather than hiding what has not been translated yet. */}
      {page.data.isFallback && (
        <Alert variant="light" color="yellow">
          {t('help.untranslated')}
        </Alert>
      )}

      {/* Sanitized server-side by the same renderer the wiki pages use. */}
      <Card withBorder padding="lg">
        <div className="compendio-content" dangerouslySetInnerHTML={{ __html: page.data.html }} />
      </Card>
    </Stack>
  );
}
