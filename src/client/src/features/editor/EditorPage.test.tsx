import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MantineProvider } from '@mantine/core';
import { ModalsProvider } from '@mantine/modals';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { EditorPage } from './EditorPage';

/**
 * That what the editor shows and what a save would write are the same text.
 *
 * @remarks
 * <p>
 * The bug this pins down was silent and expensive. The rich-text editor is uncontrolled by design —
 * it takes its document once at mount, which is what keeps the cursor alive between keystrokes — so
 * accepting an AI proposal used to update the page's state while the editor carried on showing the
 * old document. The user read one thing, pressed Save, and wrote another.
 * </p>
 * <p>
 * Nothing errored, and nothing on screen looked wrong. The only way to catch it is to assert on what
 * the editor is actually displaying after a proposal is accepted, which is what this does.
 * </p>
 */
describe('EditorPage AI proposals', () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
    window.localStorage.clear();
  });

  const path = 'IT/vpn.md';
  const original = 'The original paragraph.\n';

  function renderEditor() {
    const saved: string[] = [];

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
      const url = String(input);
      const json = (value: unknown) =>
        new Response(JSON.stringify(value), { status: 200, headers: { 'content-type': 'application/json' } });

      if (url.includes('/ai/status')) {
        return json({
          enabled: true,
          features: ['improve'],
          endpointLabel: 'localhost',
          model: 'llama3.1',
          budget: { limit: 0, used: 0, remaining: null, resetsAt: null },
        });
      }

      if (url.includes('/ai/improve')) {
        return json({ proposal: 'A much tidier paragraph.', model: 'llama3.1', endpointLabel: 'localhost' });
      }

      if (url.includes('/api/v1/pages/') && init?.method === 'PUT') {
        saved.push(JSON.parse(String(init.body)).content);
        return json({ path, title: 'VPN', contentHash: 'h2' });
      }

      if (url.includes('/api/v1/pages/')) {
        return json({
          path,
          title: 'VPN',
          tags: [],
          requiresAcknowledgment: false,
          isStale: false,
          isCanonical: true,
          contentHash: 'h1',
          byteSize: original.length,
          updatedAt: '2026-01-01T00:00:00Z',
          lastEditWasExternal: false,
          content: original,
          level: 'Manage',
        });
      }

      return json({});
    });

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MemoryRouter initialEntries={[`/edit/${path}`]}>
        <QueryClientProvider client={client}>
          <MantineProvider>
            <ModalsProvider>
              <Routes>
                <Route path="/edit/*" element={<EditorPage />} />
              </Routes>
            </ModalsProvider>
          </MantineProvider>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    return { saved };
  }

  it('shows an accepted whole-page proposal in the editor, not just in the buffer', async () => {
    renderEditor();

    // The editor opens on the real document.
    await waitFor(
      () => expect(window.document.querySelector('.ProseMirror')?.textContent)
        .toContain('The original paragraph.'),
      { timeout: 10_000 },
    );

    fireEvent.click(screen.getByText('AI'));
    fireEvent.click(await screen.findByText('Improve writing'));
    fireEvent.click(await screen.findByText('Use this'));

    // The assertion the bug would have failed: the editor itself, not the state behind it.
    await waitFor(
      () => expect(window.document.querySelector('.ProseMirror')?.textContent)
        .toContain('A much tidier paragraph.'),
      { timeout: 10_000 },
    );

    expect(window.document.querySelector('.ProseMirror')?.textContent)
      .not.toContain('The original paragraph.');
  });

  it('writes what the editor is showing when saved', async () => {
    const { saved } = renderEditor();

    await waitFor(
      () => expect(window.document.querySelector('.ProseMirror')?.textContent)
        .toContain('The original paragraph.'),
      { timeout: 10_000 },
    );

    fireEvent.click(screen.getByText('AI'));
    fireEvent.click(await screen.findByText('Improve writing'));
    fireEvent.click(await screen.findByText('Use this'));

    await waitFor(
      () => expect(window.document.querySelector('.ProseMirror')?.textContent)
        .toContain('A much tidier paragraph.'),
      { timeout: 10_000 },
    );

    fireEvent.click(screen.getByText('Save'));

    // Same text in both places. Divergence here is the whole failure mode.
    await waitFor(() => expect(saved).toHaveLength(1), { timeout: 10_000 });
    expect(saved[0]).toContain('A much tidier paragraph.');
    expect(saved[0]).not.toContain('The original paragraph.');
  });
});
