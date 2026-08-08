import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MantineProvider, createTheme } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { ModalsProvider } from '@mantine/modals';

import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import './styles.css';

import { App } from './App';
import { initI18n } from './i18n';
import { api } from './lib/api';
import { cspNonce } from './lib/csp';
import { pruneDrafts } from './features/editor/drafts';

/**
 * Logical CSS properties throughout, and no `left`/`right`.
 *
 * Costs nothing now and is the only thing that makes an Arabic or Hebrew community locale possible
 * later without a rewrite.
 */
const theme = createTheme({
  primaryColor: 'indigo',
  fontFamily: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',
  fontFamilyMonospace: 'ui-monospace, "Cascadia Code", "Source Code Pro", Menlo, monospace',
  defaultRadius: 'md',
  headings: { fontWeight: '650' },
  components: {
    // Visible focus everywhere. Accessibility is a requirement here, not a pass.
    Button: { defaultProps: { variant: 'light' } },
  },
});

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Everything is permission-dependent and the server sends no-store; a stale cache would show
      // somebody a page they can no longer read.
      staleTime: 5_000,
      retry: (failureCount, error) =>
        failureCount < 2 && !(error instanceof Error && error.name === 'ApiError'),
      refetchOnWindowFocus: false,
    },
  },
});

async function bootstrap(): Promise<void> {
  // Housekeeping, so a long-lived browser profile does not accumulate abandoned drafts.
  pruneDrafts();

  // The language has to be resolved before the first render, or the wizard flashes English at an
  // admin who chose Spanish.
  let serverDefault = 'es';
  try {
    const state = await api.setupState();
    serverDefault = state.defaultLanguage;
  } catch {
    // Server not reachable yet; fall back and let the app show its own error state.
  }

  await initI18n(serverDefault);
  document.documentElement.lang = serverDefault;

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      {/* The style element Mantine injects for its CSS variables needs the response nonce; the
          policy has no 'unsafe-inline' for style elements. */}
      <MantineProvider theme={theme} defaultColorScheme="auto" getStyleNonce={() => cspNonce ?? ''}>
        <QueryClientProvider client={queryClient}>
          <ModalsProvider>
            <Notifications position="bottom-center" />
            <BrowserRouter>
              <App />
            </BrowserRouter>
          </ModalsProvider>
        </QueryClientProvider>
      </MantineProvider>
    </StrictMode>,
  );
}

void bootstrap();
