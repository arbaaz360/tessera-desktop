import {
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
  redirect,
} from '@tanstack/react-router';
import { AppShell } from './AppShell';
import { PhotosPage } from '@/features/timeline/PhotosPage';
import { AlbumsPage } from '@/features/albums/AlbumsPage';
import { AlbumDetailPage } from '@/features/albums/AlbumDetailPage';
import { SearchPage } from '@/features/search/SearchPage';
import { LoginPage } from '@/features/auth/LoginPage';
import { TrashPage } from '@/features/trash/TrashPage';
import { AssetPage } from '@/features/viewer/AssetPage';
import { JobsPage } from '@/features/admin/JobsPage';
import { AdminPage } from '@/features/admin/AdminPage';
import { MusicPage } from '@/features/music/MusicPage';
import { FacesPage } from '@/features/faces/FacesPage';
import { getMyUser } from '@immich/sdk';

/**
 * Routing.
 *
 * Defined in code rather than generated from the filesystem. Both are valid
 * TanStack Router styles; explicit routes are easier to follow when you can see
 * the whole map in one file, and there is no codegen step to explain.
 *
 * Auth is enforced in `beforeLoad` on the protected layout route, so an
 * unauthenticated visitor is redirected before any child component mounts —
 * rather than each page checking for itself and flashing content first.
 */

const rootRoute = createRootRoute({
  component: () => <Outlet />,
});

const loginRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/login',
  component: LoginPage,
});

/**
 * Everything below this route requires a session.
 *
 * `getMyUser` doubles as the auth check: it succeeds only with a valid session
 * cookie, so one request both authenticates and gives us the current user.
 */
const authedRoute = createRoute({
  getParentRoute: () => rootRoute,
  id: 'authed',
  beforeLoad: async ({ location }) => {
    try {
      await getMyUser();
    } catch {
      throw redirect({ to: '/login', search: { redirect: location.href } });
    }
  },
  component: AppShell,
});

const indexRoute = createRoute({
  getParentRoute: () => authedRoute,
  path: '/',
  beforeLoad: () => {
    throw redirect({ to: '/photos' });
  },
});

const photosRoute = createRoute({
  getParentRoute: () => authedRoute,
  path: '/photos',
  component: PhotosPage,
});

/**
 * Deep link to one asset. External links use this shape — notably the
 * reverse-face-search sidecar, whose result links were built for the old web
 * UI's /photos/{id} URLs — and it makes a single photo shareable by address.
 */
const photoAssetRoute = createRoute({
  getParentRoute: () => authedRoute,
  path: '/photos/$assetId',
  component: AssetPage,
});

const albumsRoute = createRoute({
  getParentRoute: () => authedRoute,
  path: '/albums',
  component: AlbumsPage,
});

const albumDetailRoute = createRoute({
  getParentRoute: () => authedRoute,
  path: '/albums/$albumId',
  component: AlbumDetailPage,
});

const searchRoute = createRoute({
  getParentRoute: () => authedRoute,
  path: '/search',
  component: SearchPage,
  validateSearch: (search: Record<string, unknown>) => ({
    q: typeof search.q === 'string' ? search.q : '',
    albumId: typeof search.albumId === 'string' ? search.albumId : undefined,
  }),
});

const trashRoute = createRoute({
  getParentRoute: () => authedRoute,
  path: '/trash',
  component: TrashPage,
});

const musicRoute = createRoute({
  getParentRoute: () => authedRoute,
  path: '/music',
  component: MusicPage,
});

/**
 * Jobs and admin are separate routes rather than one page with tabs: jobs is a
 * monitoring surface someone keeps open while a queue drains, admin is a place
 * visited briefly to change a setting. Different rhythms, different pages.
 */
const facesRoute = createRoute({
  getParentRoute: () => authedRoute,
  path: '/faces',
  component: FacesPage,
  validateSearch: (search: Record<string, unknown>) => ({
    assetId: typeof search.assetId === 'string' ? search.assetId : undefined,
  }),
});

const jobsRoute = createRoute({
  getParentRoute: () => authedRoute,
  path: '/jobs',
  component: JobsPage,
});

const adminRoute = createRoute({
  getParentRoute: () => authedRoute,
  path: '/admin',
  component: AdminPage,
});

const routeTree = rootRoute.addChildren([
  loginRoute,
  authedRoute.addChildren([
    indexRoute,
    photosRoute,
    photoAssetRoute,
    albumsRoute,
    albumDetailRoute,
    searchRoute,
    trashRoute,
    musicRoute,
    facesRoute,
    jobsRoute,
    adminRoute,
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
