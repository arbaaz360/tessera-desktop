import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
  RouterProvider,
  useRouterState,
} from '@tanstack/react-router';
import { mockCore } from '@/test/render';
import type { SearchItem } from '@/shared/api/types';
import { GlobalSearch } from './GlobalSearch';
import { SearchPage } from './SearchPage';

/**
 * The header box and the search page together, in a router shaped like the
 * app's (a pathless `app` layout, so the page's `/app/search` id resolves).
 * Found in the Windows UI test (2026-10-16): typing a new file name in the
 * box while "File name" was on silently went back to searching by meaning,
 * and one hit read "1 results".
 */

const hit: SearchItem = {
  id: '7',
  ownerId: '1',
  ratio: 1.33,
  isFavorite: false,
  visibility: 'timeline',
  isTrashed: false,
  isImage: true,
  thumbhash: null,
  createdAt: '2026-10-16T08:19:31.453Z',
  fileCreatedAt: '2026-10-16T08:19:31.453Z',
  localOffsetHours: 0,
  duration: null,
  projectionType: null,
  livePhotoVideoId: null,
  score: 0,
};

function Location() {
  const search = useRouterState({ select: (state) => state.location.search });
  return <output data-testid="location">{JSON.stringify(search)}</output>;
}

function renderApp(initialPath: string) {
  const rootRoute = createRootRoute({ component: () => <Outlet /> });
  const appRoute = createRoute({
    getParentRoute: () => rootRoute,
    id: 'app',
    component: () => (
      <>
        <GlobalSearch />
        <Location />
        <Outlet />
      </>
    ),
  });
  const searchRoute = createRoute({
    getParentRoute: () => appRoute,
    path: '/search',
    component: SearchPage,
    validateSearch: (search: Record<string, unknown>) => ({
      q: typeof search.q === 'string' ? search.q : '',
      albumId: typeof search.albumId === 'string' ? search.albumId : undefined,
      similar: typeof search.similar === 'string' ? search.similar : undefined,
    }),
  });
  const photosRoute = createRoute({ getParentRoute: () => appRoute, path: '/photos', component: () => <p>photos</p> });
  const router = createRouter({
    routeTree: rootRoute.addChildren([appRoute.addChildren([searchRoute, photosRoute])]),
    history: createMemoryHistory({ initialEntries: [initialPath] }),
  });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

function location(): Record<string, unknown> {
  return JSON.parse(screen.getByTestId('location').textContent ?? '{}') as Record<string, unknown>;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('GlobalSearch on the search page', () => {
  it('keeps the file-name mode and the filters when a new term is typed', async () => {
    const user = userEvent.setup();
    const { calls } = mockCore([
      { method: 'GET', path: /^\/api\/search\?/, answer: { json: { items: [hit], model: 'siglip2-base-patch16-256' } } },
    ]);
    renderApp('/search?q=red&mode=metadata&type=IMAGE');

    // One hit is one result.
    expect(
      await screen.findByText((_, element) => element?.tagName === 'P' && element.textContent === '1 result for "red"'),
    ).toBeInTheDocument();

    await user.type(screen.getByRole('searchbox', { name: 'Search photos' }), 'img2{Enter}');

    await waitFor(() => expect(location()).toMatchObject({ q: 'img2', mode: 'metadata', type: 'IMAGE' }));
    await waitFor(() =>
      expect(calls.some((call) => call.path.startsWith('/api/search?q=img2&mode=metadata&type=IMAGE'))).toBe(true),
    );
  });

  it('starts a plain search from any other page', async () => {
    const user = userEvent.setup();
    mockCore([{ method: 'GET', path: /^\/api\/search\?/, answer: { json: { items: [], model: 'm' } } }]);
    renderApp('/photos');

    await user.type(await screen.findByRole('searchbox', { name: 'Search photos' }), 'a calm lake{Enter}');

    await waitFor(() => expect(location()).toMatchObject({ q: 'a calm lake' }));
    expect(location()).not.toHaveProperty('mode');
  });
});
