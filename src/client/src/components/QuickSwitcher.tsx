import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';
import { Kbd, Loader, Modal, Stack, Text, TextInput, UnstyledButton } from '@mantine/core';
import { useDebouncedValue, useHotkeys } from '@mantine/hooks';
import { IconSearch } from '@tabler/icons-react';

import { api, encodePath } from '../lib/api';

/**
 * Ctrl-K / Cmd-K: jump to a page.
 *
 * Backed by the same suggestion endpoint as the editor's link autocomplete, and therefore by the
 * same permission predicate. A quick switcher that showed titles the user cannot open would be a
 * page-name oracle with a keyboard shortcut.
 */
export function QuickSwitcher() {
  const { t } = useTranslation();
  const navigate = useNavigate();

  const [opened, setOpened] = useState(false);
  const [query, setQuery] = useState('');
  const [debounced] = useDebouncedValue(query, 150);
  const [highlighted, setHighlighted] = useState(0);

  useHotkeys([
    ['mod+K', () => setOpened(true)],
    ['/', () => setOpened(true)],
  ]);

  const suggestions = useQuery({
    queryKey: ['suggest', debounced],
    queryFn: () => api.suggest(debounced),
    enabled: opened && debounced.trim().length > 0,
  });

  const items = suggestions.data ?? [];

  useEffect(() => setHighlighted(0), [debounced]);

  function go(path: string) {
    setOpened(false);
    setQuery('');
    navigate(`/p/${encodePath(path)}`);
  }

  return (
    <Modal
      opened={opened}
      onClose={() => setOpened(false)}
      withCloseButton={false}
      size="lg"
      padding={0}
    >
      <Stack gap={0}>
        <TextInput
          value={query}
          onChange={(event) => setQuery(event.currentTarget.value)}
          placeholder={t('search.quickSwitcher')}
          leftSection={<IconSearch size={16} />}
          rightSection={<Kbd>Esc</Kbd>}
          size="md"
          data-autofocus
          aria-label={t('search.quickSwitcher')}
          onKeyDown={(event) => {
            if (event.key === 'ArrowDown') {
              event.preventDefault();
              setHighlighted((h) => Math.min(h + 1, items.length - 1));
            } else if (event.key === 'ArrowUp') {
              event.preventDefault();
              setHighlighted((h) => Math.max(h - 1, 0));
            } else if (event.key === 'Enter' && items[highlighted]) {
              go(items[highlighted]!.path);
            }
          }}
        />

        {suggestions.isFetching && <Loader size="xs" m="sm" />}

        {items.map((hit, index) => (
          <UnstyledButton
            key={hit.path}
            onClick={() => go(hit.path)}
            onMouseEnter={() => setHighlighted(index)}
            p="sm"
            style={{
              background:
                index === highlighted
                  ? 'light-dark(var(--mantine-color-gray-1), var(--mantine-color-dark-5))'
                  : undefined,
            }}
          >
            <Text size="sm" fw={600}>
              {hit.title}
            </Text>
            <Text size="xs" c="dimmed">
              {hit.path}
            </Text>
          </UnstyledButton>
        ))}
      </Stack>
    </Modal>
  );
}
