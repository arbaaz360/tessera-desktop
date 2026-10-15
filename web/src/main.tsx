import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { router } from './app/router';
import { configureApi } from './shared/api/client';
import { initTheme } from './shared/ui/theme';
import { watchForNewBuilds } from './shared/freshBundle';
import './styles/theme.css';

configureApi();

// Before React renders, not in an effect. Applying the stored theme after the
// first paint would flash the OS theme — a white flash on every load for anyone
// whose OS is light, which is precisely who wants dark mode.
initTheme();

// A tab that stays open for days reloads itself when a newer build is
// deployed, the next time it is looked at.
watchForNewBuilds();

/**
 * Query defaults.
 *
 * These are tuned for a photo library specifically. The data is large, changes
 * rarely, and the user scrolls back and forth constantly — so we favour serving
 * from cache and refetching seldom. A dashboard would want the opposite.
 */
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      gcTime: 10 * 60_000,
      // Refetching a 4,878-album list every time the window regains focus is
      // pure cost for data that changes a few times a day.
      refetchOnWindowFocus: false,
      retry: (failureCount, error) => {
        // Never retry auth failures — the answer will not change, and retrying
        // just delays the redirect to login.
        const status = (error as { status?: number })?.status;
        if (status === 401 || status === 403) return false;
        return failureCount < 2;
      },
    },
  },
});

const rootElement = document.getElementById('root');
if (!rootElement) throw new Error('Missing #root element');

createRoot(rootElement).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
);
