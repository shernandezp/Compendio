import { Suspense, lazy } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Center, Loader } from '@mantine/core';

import { api } from './lib/api';
import { AppShell } from './components/AppShell';
import { LoginPage } from './features/auth/LoginPage';
import { SetupWizard } from './features/setup/SetupWizard';
import { PageView } from './features/page/PageView';
import { SearchPage } from './features/search/SearchPage';

// The editor pulls in Milkdown and ProseMirror. Loading it only when somebody edits is what keeps
// the first paint on a mid-tier phone inside its budget.
const EditorPage = lazy(() => import('./features/editor/EditorPage').then((m) => ({ default: m.EditorPage })));
const HistoryPage = lazy(() => import('./features/history/HistoryPage').then((m) => ({ default: m.HistoryPage })));
const AdminPage = lazy(() => import('./features/admin/AdminPage').then((m) => ({ default: m.AdminPage })));
const AccessEditor = lazy(() => import('./features/admin/AccessEditor').then((m) => ({ default: m.AccessEditor })));
const TagBrowse = lazy(() => import('./features/search/TagBrowse').then((m) => ({ default: m.TagBrowse })));
const ProfilePage = lazy(() => import('./features/auth/ProfilePage').then((m) => ({ default: m.ProfilePage })));
const DashboardPage = lazy(() =>
  import('./features/dashboard/DashboardPage').then((m) => ({ default: m.DashboardPage })));
const NotificationsPage = lazy(() =>
  import('./features/notifications/NotificationsPage').then((m) => ({ default: m.NotificationsPage })));
const StaleReportPage = lazy(() =>
  import('./features/lifecycle/StaleReportPage').then((m) => ({ default: m.StaleReportPage })));
const AskWikiPage = lazy(() =>
  import('./features/ai/AskWikiPage').then((m) => ({ default: m.AskWikiPage })));

export function App() {
  const session = useQuery({ queryKey: ['session'], queryFn: api.session });

  if (session.isPending) {
    return (
      <Center h="100vh">
        <Loader />
      </Center>
    );
  }

  // No user exists yet: everything redirects to the wizard, which is the only screen that works.
  if (session.data?.needsSetup) {
    return (
      <Routes>
        <Route path="/setup" element={<SetupWizard />} />
        <Route path="*" element={<Navigate to="/setup" replace />} />
      </Routes>
    );
  }

  if (!session.data?.authenticated) {
    return (
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="*" element={<Navigate to="/login" replace />} />
      </Routes>
    );
  }

  return (
    <AppShell>
      <Suspense
        fallback={
          <Center h="50vh">
            <Loader />
          </Center>
        }
      >
        <Routes>
          {/* The dashboard is the landing screen: what you own, what has gone stale, what you owe. */}
          <Route path="/" element={<Navigate to="/dashboard" replace />} />
          <Route path="/dashboard" element={<DashboardPage />} />
          <Route path="/notifications" element={<NotificationsPage />} />
          <Route path="/stale" element={<StaleReportPage />} />
          <Route path="/ask" element={<AskWikiPage />} />
          <Route path="/p/*" element={<PageView />} />
          <Route path="/edit/*" element={<EditorPage />} />
          <Route path="/history/*" element={<HistoryPage />} />
          <Route path="/search" element={<SearchPage />} />
          <Route path="/tags" element={<TagBrowse />} />
          {/* Declared before /admin/* so the more specific route wins. */}
          <Route path="/admin/access/*" element={<AccessEditor />} />
          <Route path="/admin/*" element={<AdminPage />} />
          <Route path="/profile" element={<ProfilePage />} />
          <Route path="/login" element={<Navigate to="/" replace />} />
          <Route path="/setup" element={<Navigate to="/" replace />} />
        </Routes>
      </Suspense>
    </AppShell>
  );
}
