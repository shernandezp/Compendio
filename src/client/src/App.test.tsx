import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MantineProvider } from '@mantine/core';

import { App } from './App';
import { api, UNAUTHENTICATED_EVENT } from './lib/api';

function LocationProbe() {
  const { pathname, search } = useLocation();
  return <div data-testid="location">{pathname + search}</div>;
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

describe('App', () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it('sends a signed-out deep link to login and remembers where it was going', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json({ authenticated: false, needsSetup: false }));

    render(
      <MantineProvider>
        <QueryClientProvider client={new QueryClient()}>
          <MemoryRouter initialEntries={['/p/IT/vpn.md']}>
            <App />
            <LocationProbe />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    await waitFor(() =>
      expect(screen.getByTestId('location')).toHaveTextContent(`/login?next=${encodeURIComponent('/p/IT/vpn.md')}`),
    );
  });

  it('announces a 401 so the session can be re-read', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json({ title: 'Unauthorized' }, 401));
    const listener = vi.fn();
    window.addEventListener(UNAUTHENTICATED_EVENT, listener);

    await expect(api.tree()).rejects.toThrow();

    window.removeEventListener(UNAUTHENTICATED_EVENT, listener);
    expect(listener).toHaveBeenCalledOnce();
  });
});
