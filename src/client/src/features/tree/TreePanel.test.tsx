import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MantineProvider } from '@mantine/core';
import { ModalsProvider } from '@mantine/modals';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import i18next from 'i18next';
import { initReactI18next } from 'react-i18next';

import en from '../../i18n/locales/en.json';
import type { TreeNode } from '../../lib/api';
import { TreePanel } from './TreePanel';

/**
 * The tree's destructive affordances, and the one interaction that is easy to get wrong.
 *
 * A page row is a link with a menu button inside it. Getting that wrong does not throw — it
 * navigates, or it does a full page load, and the menu never opens. Worth pinning down.
 */
const node = (over: Partial<TreeNode> & { path: string }): TreeNode => ({
  name: over.path.slice(over.path.lastIndexOf('/') + 1),
  title: over.path.slice(over.path.lastIndexOf('/') + 1),
  isFolder: false,
  isSecure: false,
  level: 'Write',
  children: [],
  ...over,
});

const TREE: TreeNode[] = [
  node({
    path: 'Infrastructure',
    isFolder: true,
    level: 'Manage',
    children: [
      node({ path: 'Infrastructure/switches.md', level: 'Manage' }),
      node({
        path: 'Infrastructure/Routers',
        isFolder: true,
        level: 'Manage',
        children: [node({ path: 'Infrastructure/Routers/edge.md', level: 'Manage' })],
      }),
    ],
  }),
  node({ path: 'Runbooks', isFolder: true, level: 'Write', children: [] }),
];

function LocationProbe() {
  return <div data-testid="location">{useLocation().pathname}</div>;
}

describe('TreePanel', () => {
  beforeAll(async () => {
    await i18next.use(initReactI18next).init({
      lng: 'en',
      resources: { en: { translation: en } },
      interpolation: { escapeValue: false },
    });
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  async function renderTree(initialPath = '/dashboard', rootLevel: TreeNode['level'] = 'Manage') {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input) => {
      const url = String(input);
      if (url.includes('/api/v1/tree')) {
        return Promise.resolve(
          new Response(JSON.stringify({ rootLevel, nodes: TREE }), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          }),
        );
      }
      return Promise.resolve(new Response(null, { status: 204 }));
    });

    const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

    render(
      <MemoryRouter initialEntries={[initialPath]}>
        <QueryClientProvider client={client}>
          <MantineProvider>
            <ModalsProvider>
              <Routes>
                <Route path="*" element={<LocationProbe />} />
              </Routes>
              <TreePanel />
            </ModalsProvider>
          </MantineProvider>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await screen.findByText('Infrastructure');
    return fetchMock;
  }

  /** A folder renders its children only once open, same as for a real reader. */
  async function expand(label: string) {
    fireEvent.click(screen.getByText(label));
    await screen.findByText('switches.md');
  }

  /** Opens the ⋮ next to a row and returns the menu. */
  async function openMenu(label: string) {
    const row = screen.getByText(label).closest('a, button') as HTMLElement;
    fireEvent.click(within(row).getByLabelText(en.app.menu));
    return await screen.findByRole('menu');
  }

  it('opens a page row menu without following the link it lives inside', async () => {
    await renderTree();
    await expand('Infrastructure');

    const menu = await openMenu('switches.md');

    expect(within(menu).getByText(en.page.move)).toBeTruthy();
    expect(within(menu).getByText(en.page.delete)).toBeTruthy();

    // The click must not have navigated to the page the row links to.
    expect(screen.getByTestId('location')).toHaveTextContent('/dashboard');
  });

  it('offers folder move and delete only at manage, because that is what the server requires', async () => {
    await renderTree();

    const manageable = await openMenu('Infrastructure');
    expect(within(manageable).getByText(en.page.move)).toBeTruthy();
    expect(within(manageable).getByText(en.nav.deleteFolder)).toBeTruthy();

    fireEvent.keyDown(manageable, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('menu')).toBeNull());

    // Write is enough to add to a folder and not enough to move or delete the folder itself.
    const writable = await openMenu('Runbooks');
    expect(within(writable).getByText(en.nav.newPage)).toBeTruthy();
    expect(within(writable).queryByText(en.page.move)).toBeNull();
    expect(within(writable).queryByText(en.nav.deleteFolder)).toBeNull();
  });

  it('counts every page under a folder before deleting it, at any depth', async () => {
    await renderTree();

    const menu = await openMenu('Infrastructure');
    fireEvent.click(within(menu).getByText(en.nav.deleteFolder));

    // switches.md plus Routers/edge.md — the delete is recursive, so the nested one counts.
    expect(await screen.findByText(en.nav.deleteFolderCount_other.replace('{{count}}', '2'))).toBeTruthy();
  });

  it('deletes a page and leaves the tree to refetch', async () => {
    const fetchMock = await renderTree();
    await expand('Infrastructure');

    const menu = await openMenu('switches.md');
    fireEvent.click(within(menu).getByText(en.page.delete));

    fireEvent.click(await screen.findByRole('button', { name: en.app.delete }));

    await waitFor(() => {
      const deleted = fetchMock.mock.calls.find(
        ([, init]) => (init as RequestInit | undefined)?.method === 'DELETE',
      );
      expect(deleted).toBeTruthy();
      expect(String(deleted?.[0])).toBe('/api/v1/pages/Infrastructure/switches.md');
    });
  });

  it('leaves the deleted page rather than sitting on a path that is now a 404', async () => {
    await renderTree('/p/Infrastructure/switches.md');

    const menu = await openMenu('switches.md');
    fireEvent.click(within(menu).getByText(en.page.delete));
    fireEvent.click(await screen.findByRole('button', { name: en.app.delete }));

    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/dashboard'));
  });

  it('shows the top-of-tree New page and New folder buttons when the root is writable', async () => {
    await renderTree('/dashboard', 'Write');

    expect(screen.getByLabelText(en.nav.newPage)).toBeTruthy();
    expect(screen.getByLabelText(en.nav.newFolder)).toBeTruthy();
  });

  it('hides the top-of-tree create buttons when the user is read-only at the root', async () => {
    // The root is not a node, so these buttons create at the root. A Reader there must not be
    // invited to write a page the server will refuse to save.
    await renderTree('/dashboard', 'Read');

    expect(screen.queryByLabelText(en.nav.newPage)).toBeNull();
    expect(screen.queryByLabelText(en.nav.newFolder)).toBeNull();

    // The tree itself still renders — this hides the create affordances, not the navigation.
    expect(screen.getByText('Infrastructure')).toBeTruthy();
  });
});
