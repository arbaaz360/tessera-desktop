import { QueryClient } from '@tanstack/react-query';

/**
 * Query defaults, tuned for a picture library specifically. The data is
 * large, changes rarely, and the user scrolls back and forth constantly - so
 * we favour serving from cache and refetching seldom. A dashboard would want
 * the opposite.
 *
 * One client, in its own module, because the router's `beforeLoad` needs it
 * too (the onboarding gate reads the library through the same cache the
 * Library page fills) and a hook cannot be called from there.
 */
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      gcTime: 10 * 60_000,
      refetchOnWindowFocus: false,
      retry: (failureCount, error) => {
        // Never retry an auth failure or a "not ready": the answer will not
        // change on the next attempt, and the page shows the real state.
        const status = (error as { status?: number })?.status;
        if (status === 401 || status === 403 || status === 503) return false;
        return failureCount < 2;
      },
    },
  },
});
