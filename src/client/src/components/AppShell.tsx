import { type ReactNode, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  ActionIcon,
  AppShell as MantineAppShell,
  Burger,
  Group,
  Menu,
  Text,
  TextInput,
  Tooltip,
  useMantineColorScheme,
} from '@mantine/core';
import { useDisclosure, useMediaQuery } from '@mantine/hooks';
import {
  IconHelp,
  IconMessageQuestion,
  IconMoon,
  IconSearch,
  IconSettings,
  IconSun,
  IconUser,
} from '@tabler/icons-react';

import { api } from '../lib/api';
import { changeLanguage, SUPPORTED_LANGUAGES } from '../i18n';
import { NotificationBell } from '../features/notifications/NotificationBell';
import { TreePanel } from '../features/tree/TreePanel';
import { aiFeatures, useAiStatus } from '../features/ai/useAi';
import { QuickSwitcher } from './QuickSwitcher';

/**
 * One responsive shell, no separate mobile build.
 *
 * On a phone the tree collapses into a drawer rather than becoming a second navigation model — the
 * acceptance scenario is a technician at a server rack opening a runbook one-handed, and a second
 * UI is a second thing to get wrong.
 */
export function AppShell({ children }: { children: ReactNode }) {
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [opened, { toggle, close }] = useDisclosure();
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const isMobile = useMediaQuery('(max-width: 48em)');
  const [query, setQuery] = useState('');

  const session = useQuery({ queryKey: ['session'], queryFn: api.session });
  const about = useQuery({ queryKey: ['about'], queryFn: api.about });
  const ai = useAiStatus();

  const isAdmin = session.data?.user?.role === 'Admin';

  async function signOut() {
    await api.logout();
    await queryClient.invalidateQueries();
    navigate('/login');
  }

  function submitSearch(event: React.FormEvent) {
    event.preventDefault();
    navigate(`/search?q=${encodeURIComponent(query)}`);
    close();
  }

  return (
    <MantineAppShell
      header={{ height: 56 }}
      navbar={{ width: 300, breakpoint: 'sm', collapsed: { mobile: !opened } }}
      padding="md"
    >
      <a href="#main" className="skip-link">
        {t('app.skipToContent')}
      </a>

      <QuickSwitcher />

      <MantineAppShell.Header>
        <Group h="100%" px="md" gap="sm" wrap="nowrap">
          <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" aria-label={t('app.menu')} />

          <Text component={Link} to="/" fw={700} size="lg" style={{ textDecoration: 'none' }}>
            {about.data?.instanceName ?? t('app.name')}
          </Text>

          <form onSubmit={submitSearch} style={{ flex: 1, maxWidth: 520 }}>
            <TextInput
              value={query}
              onChange={(event) => setQuery(event.currentTarget.value)}
              placeholder={t('search.placeholder')}
              aria-label={t('app.search')}
              leftSection={<IconSearch size={16} />}
              size="sm"
            />
          </form>

          <Group gap="xs" ml="auto" wrap="nowrap">
            {/* Next to the search box, because that is the same question asked a different way —
                and because a feature reachable only by typing its URL is a feature nobody uses.
                Absent entirely when no provider is configured, like every other AI control. */}
            {ai.has(aiFeatures.ask) && (
              <Tooltip label={t('nav.ask')}>
                <ActionIcon
                  component={Link}
                  to="/ask"
                  variant="subtle"
                  aria-label={t('nav.ask')}
                  onClick={close}
                >
                  <IconMessageQuestion size={18} />
                </ActionIcon>
              </Tooltip>
            )}

            {/* Always present, unlike the AI control above it — the guide describes the product,
                not an optional feature, and help that only some instances have is not help. */}
            <Tooltip label={t('help.title')}>
              <ActionIcon component={Link} to="/help" variant="subtle" aria-label={t('help.title')} onClick={close}>
                <IconHelp size={18} />
              </ActionIcon>
            </Tooltip>

            <Tooltip label={t('app.theme.label')}>
              <ActionIcon
                variant="subtle"
                onClick={() => setColorScheme(colorScheme === 'dark' ? 'light' : 'dark')}
                aria-label={t('app.theme.label')}
              >
                {colorScheme === 'dark' ? <IconSun size={18} /> : <IconMoon size={18} />}
              </ActionIcon>
            </Tooltip>

            <Menu position="bottom-end">
              <Menu.Target>
                <ActionIcon variant="subtle" aria-label={t('app.language.label')}>
                  <Text size="xs" fw={700}>
                    {i18n.language.toUpperCase()}
                  </Text>
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                {SUPPORTED_LANGUAGES.map((code) => (
                  <Menu.Item key={code} onClick={() => changeLanguage(code)}>
                    {code === 'es' ? 'Español' : 'English'}
                  </Menu.Item>
                ))}
              </Menu.Dropdown>
            </Menu>

            <NotificationBell />

            {isAdmin && (
              <Tooltip label={t('admin.title')}>
                <ActionIcon component={Link} to="/admin" variant="subtle" aria-label={t('admin.title')}>
                  <IconSettings size={18} />
                </ActionIcon>
              </Tooltip>
            )}

            <Menu position="bottom-end">
              <Menu.Target>
                <ActionIcon variant="subtle" aria-label={t('auth.profile')}>
                  <IconUser size={18} />
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Label>{session.data?.user?.displayName}</Menu.Label>
                <Menu.Item component={Link} to="/profile">
                  {t('auth.profile')}
                </Menu.Item>
                <Menu.Divider />
                <Menu.Item onClick={() => void signOut()}>{t('auth.signOut')}</Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </Group>
        </Group>
      </MantineAppShell.Header>

      <MantineAppShell.Navbar p="sm">
        <TreePanel onNavigate={isMobile ? close : undefined} />
      </MantineAppShell.Navbar>

      <MantineAppShell.Main id="main">
        {children}

        {/* The notice the AGPL requires of a running program (§5d). */}
        <Text size="xs" c="dimmed" mt="xl" ta="center">
          {about.data?.licenseNotice}
        </Text>
      </MantineAppShell.Main>
    </MantineAppShell>
  );
}
