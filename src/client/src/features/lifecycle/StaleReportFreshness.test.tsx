import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';

import { StaleReportPage } from './StaleReportPage';

/**
 * Freshness hints on the stale report: present when the feature is on, absent when it is not.
 *
 * @remarks
 * The absence case is the one worth a test. Every other AI control in the product renders nothing
 * when no provider is configured, and this one is easy to get wrong in a way that looks fine: a
 * column of empty cells, or fifty buttons that 404 when pressed. Both would pass an eyeball on a
 * developer's machine, where AI is always configured.
 */
describe('StaleReportPage freshness hints', () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  const row = {
    path: 'IT/vpn.md',
    title: 'VPN setup',
    owner: 'ana',
    ownerDisplayName: 'Ana Rodríguez',
    unassigned: false,
    nextReviewDate: '2026-01-01T00:00:00Z',
    daysOverdue: 120,
    updatedAt: '2025-12-01T00:00:00Z',
  };

  const renderWith = (features: string[]) => {
    const sent: string[] = [];

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      const url = String(input);
      const json = (value: unknown) =>
        new Response(JSON.stringify(value), { status: 200, headers: { 'content-type': 'application/json' } });

      if (url.includes('/ai/status')) {
        return json({
          enabled: features.length > 0,
          features,
          endpointLabel: 'localhost',
          model: 'llama3.1',
          budget: { limit: 0, used: 0, remaining: null, resetsAt: null },
        });
      }

      if (url.includes('/lifecycle/stale')) {
        return json({ items: [row], totalCount: 1, page: 1, pageSize: 50 });
      }

      sent.push(url);
      return json({ proposal: 'Mentions Windows Server 2012.', model: 'llama3.1', endpointLabel: 'localhost' });
    });

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <MantineProvider>
            <StaleReportPage />
          </MantineProvider>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    return { sent };
  };

  it('offers no freshness control when no provider is configured', async () => {
    renderWith([]);

    await waitFor(() => expect(screen.getByText('VPN setup')).toBeTruthy());

    expect(screen.queryByLabelText('Check for outdated content')).toBeNull();
  });

  it('offers no freshness control when the feature alone is switched off', async () => {
    // The narrower case: AI is configured, but an administrator has unchecked this one feature.
    renderWith(['improve', 'summarize']);

    await waitFor(() => expect(screen.getByText('VPN setup')).toBeTruthy());

    expect(screen.queryByLabelText('Check for outdated content')).toBeNull();
  });

  it('checks the row it sits on and shows the result read-only', async () => {
    const { sent } = renderWith(['freshness']);

    const button = await screen.findByLabelText('Check for outdated content');
    fireEvent.click(button);

    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toContain('/ai/freshness');

    expect(await screen.findByText('Mentions Windows Server 2012.')).toBeTruthy();

    // A hint has nowhere to be applied, so the dialog offers no way to apply it.
    expect(screen.getByText('Close')).toBeTruthy();
    expect(screen.queryByText('Use this')).toBeNull();
  });
});
