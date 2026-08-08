import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';

import { AiMenu } from './AiMenu';

/**
 * Criterion 6, the client half: with no provider configured, no AI affordance renders anywhere.
 *
 * Worth a test rather than an eyeball, because the failure is silent and permanent — a button that
 * only fails when pressed looks like a working feature until somebody presses it, and every AI
 * action returns 404 in that state.
 *
 * The second case matters as much as the first: a suite that only checked absence would pass
 * trivially if the menu were broken for everyone.
 *
 * The selection cases guard the newer and quieter failure: an action invoked on a highlight but
 * accepted over the whole page, which loses work without erroring.
 */
describe('AiMenu', () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  interface Status {
    enabled: boolean;
    features: string[];
    endpointLabel: string;
    model: string;
    budget?: { limit: number; used: number; remaining: number | null; resetsAt: string | null };
  }

  const enabled: Status = {
    enabled: true,
    features: ['improve', 'summarize', 'freshness'],
    endpointLabel: 'localhost',
    model: 'llama3.1',
    budget: { limit: 0, used: 0, remaining: null, resetsAt: null },
  };

  /**
   * Answers `/ai/status` from the given object and every AI action with a fixed proposal, recording
   * the request bodies so a test can assert on what was actually sent.
   */
  const renderWith = (
    status: Status,
    props: Partial<Parameters<typeof AiMenu>[0]> = {},
  ) => {
    const sent: { url: string; body: unknown }[] = [];

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
      const url = String(input);
      const json = (value: unknown) =>
        new Response(JSON.stringify(value), { status: 200, headers: { 'content-type': 'application/json' } });

      if (url.includes('/ai/status')) {
        return json(status);
      }

      sent.push({ url, body: init?.body ? JSON.parse(String(init.body)) : undefined });

      return json({ proposal: 'A tidier paragraph.', model: 'llama3.1', endpointLabel: 'localhost' });
    });

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    const view = render(
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <MantineProvider>
            {/* Scoped, because MantineProvider injects its own <style> tags into the container and
                an "is the container empty" assertion would be asserting on those. */}
            <div data-testid="ai-slot">
              <AiMenu path="IT/vpn.md" onAccept={() => undefined} {...props} />
            </div>
          </MantineProvider>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    return { ...view, sent };
  };

  it('renders nothing at all when no provider is configured', async () => {
    renderWith({ enabled: false, features: [], endpointLabel: '', model: '' });

    // Settled, so this is "it stayed empty" rather than "it had not rendered yet".
    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalled());

    expect(screen.getByTestId('ai-slot')).toBeEmptyDOMElement();
    expect(screen.queryByRole('button')).toBeNull();
  });

  it('renders the menu once a provider is configured', async () => {
    renderWith(enabled);

    await waitFor(() => expect(screen.getByRole('button')).toBeTruthy());
  });

  it('sends the whole page when nothing is highlighted', async () => {
    const { sent } = renderWith(enabled);

    await waitFor(() => expect(screen.getByRole('button')).toBeTruthy());
    fireEvent.click(screen.getByRole('button'));
    fireEvent.click(await screen.findByText('Improve writing'));

    await waitFor(() => expect(sent).toHaveLength(1));

    // `text` absent is what the server reads as "use the page body".
    expect((sent[0]!.body as { text?: string }).text).toBeUndefined();
  });

  /**
   * The one that matters: a highlight has to reach the request, or "Improve selection" is a label
   * over a whole-page rewrite.
   */
  it('sends only the highlight when there is one, and scopes the proposal to it', async () => {
    const accepted: { markdown: string; scope: string }[] = [];

    const { sent } = renderWith(enabled, {
      selection: { active: true, read: () => 'The paragraph they highlighted.' },
      onAccept: (markdown, scope) => accepted.push({ markdown, scope }),
    });

    await waitFor(() => expect(screen.getByRole('button')).toBeTruthy());
    fireEvent.click(screen.getByRole('button'));

    // The label changes with the highlight, because "Improve writing" over a selection reads as an
    // offer to rewrite the page.
    fireEvent.click(await screen.findByText('Improve selection'));

    await waitFor(() => expect(sent).toHaveLength(1));
    expect((sent[0]!.body as { text?: string }).text).toBe('The paragraph they highlighted.');

    fireEvent.click(await screen.findByText('Replace selection'));

    expect(accepted).toEqual([{ markdown: 'A tidier paragraph.', scope: 'selection' }]);
  });

  /**
   * A highlight that disappeared between opening the menu and choosing an item falls back to the
   * page — and the proposal must fall back with it, or an accepted rewrite lands in a range that no
   * longer exists.
   */
  it('falls back to the page when the highlight has gone by the time the action runs', async () => {
    const accepted: { markdown: string; scope: string }[] = [];

    const { sent } = renderWith(enabled, {
      selection: { active: true, read: () => '' },
      onAccept: (markdown, scope) => accepted.push({ markdown, scope }),
    });

    await waitFor(() => expect(screen.getByRole('button')).toBeTruthy());
    fireEvent.click(screen.getByRole('button'));
    fireEvent.click(await screen.findByText('Improve selection'));

    await waitFor(() => expect(sent).toHaveLength(1));
    expect((sent[0]!.body as { text?: string }).text).toBeUndefined();

    fireEvent.click(await screen.findByText('Use this'));

    expect(accepted).toEqual([{ markdown: 'A tidier paragraph.', scope: 'page' }]);
  });

  /**
   * Cancel has to abort the request, not just stop listening to it.
   *
   * @remarks
   * The difference is invisible on screen and is the whole feature: a cancel that only hides the
   * spinner leaves the provider generating tokens the user has already been charged for and has just
   * said they do not want. Asserting on the signal is the only way to tell the two apart.
   */
  it('aborts the in-flight request when cancelled, and reports nothing afterwards', async () => {
    let captured: AbortSignal | undefined;

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
      const url = String(input);

      if (url.includes('/ai/status')) {
        return new Response(JSON.stringify(enabled), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        });
      }

      captured = init?.signal ?? undefined;

      // Never resolves on its own — the only way out is the abort, exactly like a model that is
      // still thinking.
      return new Promise<Response>((_, reject) => {
        init?.signal?.addEventListener('abort', () =>
          reject(Object.assign(new Error('aborted'), { name: 'AbortError' })));
      });
    });

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <MantineProvider>
            <AiMenu path="IT/vpn.md" onAccept={() => undefined} />
          </MantineProvider>
        </QueryClientProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByRole('button')).toBeTruthy());
    fireEvent.click(screen.getByRole('button'));
    fireEvent.click(await screen.findByText('Improve writing'));

    // The trigger becomes the way out, rather than a second control widening the toolbar.
    const cancel = await screen.findByText('Cancel');
    expect(captured?.aborted).toBe(false);

    fireEvent.click(cancel);

    await waitFor(() => expect(captured?.aborted).toBe(true));

    // And back to the menu, with no error surfaced — the user asked for this.
    await waitFor(() => expect(screen.getByText('AI')).toBeTruthy());
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('shows what is left of the allowance only once it is nearly spent', async () => {
    const { unmount } = renderWith({
      ...enabled,
      budget: { limit: 50, used: 10, remaining: 40, resetsAt: null },
    });

    await waitFor(() => expect(screen.getByRole('button')).toBeTruthy());
    fireEvent.click(screen.getByRole('button'));

    expect(screen.queryByText(/AI requests left/)).toBeNull();

    unmount();
    cleanup();
    vi.restoreAllMocks();

    renderWith({ ...enabled, budget: { limit: 50, used: 48, remaining: 2, resetsAt: null } });

    await waitFor(() => expect(screen.getByRole('button')).toBeTruthy());
    fireEvent.click(screen.getByRole('button'));

    expect(await screen.findByText(/2 of 50 AI requests left/)).toBeTruthy();
  });
});
