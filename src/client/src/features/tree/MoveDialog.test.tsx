import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MantineProvider } from '@mantine/core';
import i18next from 'i18next';
import { initReactI18next } from 'react-i18next';

import en from '../../i18n/locales/en.json';
import type { TreeNode } from '../../lib/api';
import { MoveDialog } from './MoveDialog';

/**
 * The destination list is the whole safety story of this dialog, so it is what gets tested.
 *
 * The server checks both ends and rejects a loop, but "the server said no" arrives after somebody
 * has already decided where their document should go. These cases are about never offering the
 * choice in the first place.
 */
const folder = (path: string, children: TreeNode[] = [], level: TreeNode['level'] = 'Write'): TreeNode => ({
  path,
  name: path.slice(path.lastIndexOf('/') + 1),
  title: path.slice(path.lastIndexOf('/') + 1),
  isFolder: true,
  isSecure: false,
  level,
  children,
});

const page = (path: string, level: TreeNode['level'] = 'Write'): TreeNode => ({
  path,
  name: path.slice(path.lastIndexOf('/') + 1),
  title: 'A page',
  isFolder: false,
  isSecure: false,
  level,
  children: [],
});

const TREE: TreeNode[] = [
  folder('Infrastructure', [folder('Infrastructure/Routers'), page('Infrastructure/switches.md')]),
  folder('Runbooks', [], 'Read'),
];

describe('MoveDialog', () => {
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

  function renderDialog(node: TreeNode, onMoved = vi.fn()) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

    render(
      <QueryClientProvider client={client}>
        <MantineProvider>
          <MoveDialog node={node} tree={TREE} onClose={() => undefined} onMoved={onMoved} />
        </MantineProvider>
      </QueryClientProvider>,
    );

    return { onMoved };
  }

  const ok = (body: unknown) =>
    new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });

  it('does not offer a folder its own subtree, which would be a move into itself', () => {
    renderDialog(folder('Infrastructure'));

    expect(screen.queryByRole('radio', { name: /Infrastructure$/ })).toBeNull();
    expect(screen.queryByRole('radio', { name: /Routers/ })).toBeNull();

    // The rest of the tree is still reachable, so this is an exclusion and not an empty list.
    expect(screen.getByRole('radio', { name: /Top level/ })).toBeTruthy();
  });

  it('disables a destination the caller cannot write to', () => {
    renderDialog(page('Infrastructure/switches.md'));

    expect(screen.getByRole('radio', { name: /Runbooks/ })).toBeDisabled();
    expect(screen.getByRole('radio', { name: /Routers/ })).toBeEnabled();
  });

  it('moves a page into the chosen folder, keeping the .md extension off the name field', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(ok({ path: 'Runbooks/switches.md' }));
    const { onMoved } = renderDialog(page('Infrastructure/switches.md'));

    // The extension is never in the editable text — it is shown beside the field.
    expect(screen.getByLabelText('Name')).toHaveValue('switches');

    fireEvent.click(screen.getByRole('radio', { name: /Routers/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Move' }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/v1/pages/move');
    expect(JSON.parse(init.body as string)).toEqual({
      path: 'Infrastructure/switches.md',
      targetPath: 'Infrastructure/Routers/switches.md',
    });

    await waitFor(() => expect(onMoved).toHaveBeenCalledWith('Infrastructure/Routers/switches.md'));
  });

  it('renames in place, and refuses a move that would change nothing', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(ok({ path: 'Infrastructure/core.md' }));
    renderDialog(page('Infrastructure/switches.md'));

    // Same folder, same name: there is nothing to do, and the button says so.
    expect(screen.getByRole('button', { name: 'Move' })).toBeDisabled();

    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'core' } });
    fireEvent.click(screen.getByRole('button', { name: 'Move' }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string).targetPath).toBe('Infrastructure/core.md');
  });

  it('refuses a name with a path separator, which would silently pick a different destination', () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch');
    renderDialog(page('Infrastructure/switches.md'));

    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'reports/q3' } });

    expect(screen.getByText(en.page.moveNameSeparator)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Move' })).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Move' }));
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('refuses a name the content folder cannot hold', () => {
    renderDialog(page('Infrastructure/switches.md'));

    for (const bad of ['what?', 'a<b', 'notes..md', 'trailing.']) {
      fireEvent.change(screen.getByLabelText('Name'), { target: { value: bad } });
      expect(screen.getByText(en.page.moveNameIllegal)).toBeTruthy();
    }

    // Spaces and hyphens are legal, so the guard must not be a blanket refusal.
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'core switch-01' } });
    expect(screen.queryByText(en.page.moveNameIllegal)).toBeNull();
    expect(screen.getByRole('button', { name: 'Move' })).toBeEnabled();
  });

  it('explains a name collision rather than showing the generic failure', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ code: 'path.exists', title: 'Exists' }), {
        status: 400,
        headers: { 'content-type': 'application/problem+json' },
      }),
    );
    renderDialog(page('Infrastructure/switches.md'));

    fireEvent.click(screen.getByRole('radio', { name: /Top level/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Move' }));

    expect(await screen.findByText(en.page.moveExists)).toBeTruthy();
  });
});
