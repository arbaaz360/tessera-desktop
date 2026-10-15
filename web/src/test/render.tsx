import type { ReactNode } from 'react';
import { render } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
  RouterProvider,
} from '@tanstack/react-router';
import { vi } from 'vitest';
import { configureApi } from '@/shared/api/client';

/**
 * Test plumbing shared by the page tests.
 *
 * Pages use TanStack Query for data and TanStack Router for links and
 * navigation, so a page under test needs both providers. `renderPage` gives
 * it a fresh query client (no retries, so a 4xx fails fast) and a memory
 * router whose only real route renders the page; links to other routes
 * resolve to hrefs without needing the whole app tree.
 *
 * `mockCore` replaces `fetch` with a router of its own: a list of
 * `[method, path, answer]` rules matched in order. Paths are matched against
 * the request path, query string included, so a test can tell
 * `/api/library` from `/api/library/duplicates?limit=200`. The core origin
 * and token are configured so the real client code runs end to end.
 */

export const TEST_CORE = { baseUrl: 'http://core.test', token: 'test-token' };

type Answer =
  | { status?: number; json?: unknown }
  | ((init: RequestInit | undefined, url: URL) => { status?: number; json?: unknown });

export interface Rule {
  method: string;
  path: string | RegExp;
  answer: Answer;
}

export interface Call {
  method: string;
  path: string;
  body: unknown;
  headers: Headers;
}

export function mockCore(rules: Rule[]) {
  configureApi(TEST_CORE);
  const calls: Call[] = [];

  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url);
    const method = (init?.method ?? 'GET').toUpperCase();
    const pathWithQuery = `${url.pathname}${url.search}`;
    const headers = new Headers(init?.headers);
    const body = typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined;
    calls.push({ method, path: pathWithQuery, body, headers });

    const rule = rules.find(
      (candidate) =>
        candidate.method === method &&
        (typeof candidate.path === 'string'
          ? candidate.path === pathWithQuery || candidate.path === url.pathname
          : candidate.path.test(pathWithQuery)),
    );
    if (!rule) {
      return new Response(JSON.stringify({ detail: `no rule for ${method} ${pathWithQuery}` }), {
        status: 404,
        headers: { 'content-type': 'application/json' },
      });
    }
    const answer = typeof rule.answer === 'function' ? rule.answer(init, url) : rule.answer;
    const status = answer.status ?? 200;
    if (status === 204 || answer.json === undefined) return new Response(null, { status });
    return new Response(JSON.stringify(answer.json), {
      status,
      headers: { 'content-type': 'application/json' },
    });
  });

  vi.stubGlobal('fetch', fetchMock);
  return { calls, fetchMock };
}

export function renderPage(ui: ReactNode, initialPath = '/') {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { retry: false } },
  });

  const rootRoute = createRootRoute({ component: () => <Outlet /> });
  const pageRoute = createRoute({
    getParentRoute: () => rootRoute,
    path: initialPath,
    component: () => <>{ui}</>,
  });
  // Destinations the pages link to. They render nothing; they exist so a
  // navigation in a test lands somewhere observable instead of "not found".
  const sink = (path: string) =>
    createRoute({
      getParentRoute: () => rootRoute,
      path,
      component: () => <p data-testid="navigated-to">{path}</p>,
    });
  const extra = ['/photos', '/settings', '/library', '/welcome', '/search', '/albums'].filter(
    (path) => path !== initialPath,
  );

  const router = createRouter({
    routeTree: rootRoute.addChildren([pageRoute, ...extra.map(sink)]),
    history: createMemoryHistory({ initialEntries: [initialPath] }),
  });

  const view = render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return { ...view, client, router };
}
