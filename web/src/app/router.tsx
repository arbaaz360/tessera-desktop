import {
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
  redirect,
} from '@tanstack/react-router';
import { AppShell } from './AppShell';
import { queryClient } from './queryClient';
import { apiJson } from '@/shared/api/tessera';
import { qk } from '@/shared/api/queries';
import type { Library } from '@/shared/api/types';
import { PhotosPage } from '@/features/timeline/PhotosPage';
import { AlbumsPage } from '@/features/albums/AlbumsPage';
import { AlbumDetailPage } from '@/features/albums/AlbumDetailPage';
import { SearchPage } from '@/features/search/SearchPage';
import { TrashPage } from '@/features/trash/TrashPage';
import { AssetPage } from '@/features/viewer/AssetPage';
import { LibraryPage } from '@/features/library/LibraryPage';
import { SettingsPage } from '@/features/settings/SettingsPage';
import { OnboardingPage } from '@/features/onboarding/OnboardingPage';
import { welcomeSkipped } from '@/features/onboarding/welcome';

/**
 * Routing.
 *
 * Defined in code rather than generated from the filesystem: explicit routes
 * are easier to follow when you can see the whole map in one file, and there
 * is no codegen step to explain.
 *
 * There is no login. The boot token is applied before the router mounts (see
 * AppBoot). A library with no roots opens on /welcome, ONCE: the welcome page
 * can be skipped, and pictures can be dropped or picked onto the empty grid,
 * so a folder is not the only way in (`features/onboarding/welcome.ts`). The
 * check reads the library through the same query cache the Library page
 * fills, so it is one request per session, not one per navigation.
 */

const rootRoute = createRootRoute({
  component: () => <Outlet />,
});

/** First run: pick a folder, watch the model arrive. Full screen, no shell. */
const welcomeRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/welcome',
  component: OnboardingPage,
});

const appRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: 'app',
  beforeLoad: async () => {
    let library: Library;
    try {
      library = await queryClient.ensureQueryData({
        queryKey: qk.library.summary(),
        queryFn: () => apiJson<Library>('/api/library'),
      });
    } catch {
      // The core is unreachable or unhappy; let the page say so rather than
      // bouncing a person with a library to the welcome screen.
      return;
    }
    if (library.roots.length === 0 && !welcomeSkipped()) {
      throw redirect({ to: '/welcome' });
    }
  },
  component: AppShell,
});

const indexRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/',
  beforeLoad: () => {
    throw redirect({ to: '/photos' });
  },
});

const photosRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/photos',
  component: PhotosPage,
});

/** Deep link to one picture. */
const photoAssetRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/photos/$assetId',
  component: AssetPage,
});

const albumsRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/albums',
  component: AlbumsPage,
});

const albumDetailRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/albums/$albumId',
  component: AlbumDetailPage,
});

/**
 * Search carries three things in the URL: the words, an album scope, and -
 * for "more like this" - the picture whose vector is the query. The filter
 * params ride alongside untyped; see features/search/searchParams.ts.
 */
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

const trashRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/trash',
  component: TrashPage,
});

const libraryRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/library',
  component: LibraryPage,
});

const settingsRoute = createRoute({
  getParentRoute: () => appRoute,
  path: '/settings',
  component: SettingsPage,
});

const routeTree = rootRoute.addChildren([
  welcomeRoute,
  appRoute.addChildren([
    indexRoute,
    photosRoute,
    photoAssetRoute,
    albumsRoute,
    albumDetailRoute,
    searchRoute,
    trashRoute,
    libraryRoute,
    settingsRoute,
  ]),
]);

export const router = createRouter({
  routeTree,
  defaultPreload: 'intent',
  // Data is owned by TanStack Query, not the router loader, so the router does
  // not need its own staleness policy.
  defaultPreloadStaleTime: 0,
});

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router;
  }
}
