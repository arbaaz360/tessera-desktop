import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  createMemoryHistory,
  createRootRoute,
  createRoute,
  createRouter,
  Outlet,
  RouterProvider,
} from '@tanstack/react-router';
import { render } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mockCore, renderPage } from '@/test/render';
import type { AlbumDto, ImportJob } from '@/shared/api/types';
import { qk } from '@/shared/api/queries';
import { useBulkAddToAlbum, useBulkRemoveFromAlbum } from '@/features/selection/api';
import { AlbumDetailPage } from '@/features/albums/AlbumDetailPage';
import { ImportProvider } from './ImportContext';
import { ImportToasts } from './ImportToasts';
import { DropZone } from './DropZone';

afterEach(() => {
  vi.unstubAllGlobals();
  document.body.innerHTML = '';
});

const job = (patch: Partial<ImportJob>): ImportJob => ({
  id: 7,
  state: 'running',
  startedAt: '2026-10-18T10:00:00.000Z',
  total: 3,
  done: 0,
  counts: { added: 0, already: 0, copied: 0, skipped: 0, folders: 0 },
  items: [],
  ...patch,
});

/** A drag event from outside the page, as a webview delivers one: files or a link. */
function dragEvent(type: string, data: { files?: File[]; uriList?: string }) {
  const event = new Event(type, { bubbles: true, cancelable: true });
  const types = [...(data.files?.length ? ['Files'] : []), ...(data.uriList ? ['text/uri-list'] : [])];
  Object.defineProperty(event, 'dataTransfer', {
    value: {
      types,
      files: data.files ?? [],
      items: data.files ?? [],
      dropEffect: 'none',
      getData: (format: string) => (format === 'text/uri-list' ? (data.uriList ?? '') : ''),
    },
  });
  return event;
}

function withImports(ui: ReactNode) {
  return (
    <ImportProvider>
      {ui}
      <DropZone />
      <ImportToasts />
    </ImportProvider>
  );
}

describe('dropping onto the window', () => {
  it('shows where the drop goes, uploads the files to that album, and reports the counts', async () => {
    const { calls } = mockCore([
      {
        method: 'POST',
        path: /^\/api\/import\/upload\?/,
        answer: (_init, url) => ({
          json: job({
            id: 9,
            state: 'done',
            total: 1,
            done: 1,
            counts: { added: 0, already: 0, copied: 1, skipped: 0, folders: 0 },
            items: [{ source: url.searchParams.get('name') ?? '', kind: 'upload', outcome: 'copied', assetId: '42' }],
          }),
        }),
      },
    ]);

    const { client } = renderPage(
      withImports(
        <div data-drop-album-id="5" data-drop-album-name="A">
          <p>album card</p>
        </div>,
      ),
    );
    const spy = vi.spyOn(client, 'invalidateQueries');
    const card = await screen.findByText('album card');
    const files = [new File(['one'], 'one.jpg', { type: 'image/jpeg' }), new File(['two'], 'two.mp4', { type: 'video/mp4' })];

    act(() => {
      card.dispatchEvent(dragEvent('dragenter', { files }));
    });
    expect(await screen.findByText('Drop to add 2 items to “A”')).toBeInTheDocument();
    expect(card.parentElement).toHaveAttribute('data-drop-hover');

    act(() => {
      card.dispatchEvent(dragEvent('drop', { files }));
    });

    expect(await screen.findByText('Added to “A”')).toBeInTheDocument();
    expect(screen.getByText(/2 of 2 · 2 copied in/)).toBeInTheDocument();
    expect(screen.queryByTestId('drop-overlay')).not.toBeInTheDocument();
    expect(card.parentElement).not.toHaveAttribute('data-drop-hover');

    const uploads = calls.filter((call) => call.path.startsWith('/api/import/upload'));
    expect(uploads.map((call) => call.path)).toEqual([
      '/api/import/upload?name=one.jpg&albumId=5',
      '/api/import/upload?name=two.mp4&albumId=5',
    ]);
    expect(uploads[0]!.headers.get('content-type')).toBe('application/octet-stream');
    await waitFor(() => expect(spy).toHaveBeenCalledWith({ queryKey: qk.timeline.all }));
    expect(spy).toHaveBeenCalledWith({ queryKey: qk.albums.all });
    expect(spy).toHaveBeenCalledWith({ queryKey: qk.library.all });
  });

  it('sends a dropped link to the core to fetch once, and ignores drags that start on the page', async () => {
    const { calls } = mockCore([
      { method: 'POST', path: '/api/import', answer: { status: 202, json: job({ id: 3, total: 1 }) } },
      {
        method: 'GET',
        path: '/api/import/3',
        answer: {
          json: job({
            id: 3,
            state: 'done',
            total: 1,
            done: 1,
            counts: { added: 0, already: 0, copied: 0, skipped: 1, folders: 0 },
            items: [{ source: 'https://example.test/page', kind: 'url', outcome: 'skipped', reason: 'not a picture or video (text/html)' }],
          }),
        },
      },
    ]);
    renderPage(withImports(<p>grid</p>));
    const grid = await screen.findByText('grid');

    // A thumbnail dragged a few pixels inside the page is not an import.
    act(() => {
      grid.dispatchEvent(new Event('dragstart', { bubbles: true }));
      grid.dispatchEvent(dragEvent('dragenter', { files: [new File(['x'], 'x.png')] }));
    });
    expect(screen.queryByTestId('drop-overlay')).not.toBeInTheDocument();
    act(() => {
      grid.dispatchEvent(new Event('dragend', { bubbles: true }));
    });

    act(() => {
      grid.dispatchEvent(dragEvent('drop', { uriList: 'https://example.test/page' }));
    });

    expect(await screen.findByText(/not a picture or video \(text\/html\)/, {}, { timeout: 3000 })).toBeInTheDocument();
    expect(calls.find((call) => call.method === 'POST')?.body).toEqual({ urls: ['https://example.test/page'] });
  });
});

describe('album membership refreshes the grids', () => {
  it.each([
    ['add', useBulkAddToAlbum, 'PUT'],
    ['remove', useBulkRemoveFromAlbum, 'DELETE'],
  ] as const)('%s invalidates the timeline as well as the album list', async (_name, hook, method) => {
    mockCore([{ method, path: '/api/albums/5/assets', answer: { json: [{ id: '1', success: true }] } }]);
    const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    const spy = vi.spyOn(client, 'invalidateQueries');
    const { result } = renderHook(() => hook(), {
      wrapper: ({ children }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>,
    });

    await act(() => result.current.mutateAsync({ albumId: '5', ids: ['1'] }));

    expect(spy).toHaveBeenCalledWith({ queryKey: qk.timeline.all });
    expect(spy).toHaveBeenCalledWith({ queryKey: qk.albums.all });
  });
});

describe('the album page', () => {
  it('has no second search box, takes drops for itself, and its empty state offers both buttons', async () => {
    const album: AlbumDto = {
      id: '5',
      albumName: 'A',
      description: '',
      ownerId: '1',
      albumThumbnailAssetId: null,
      createdAt: '2026-10-17T00:00:00.000Z',
      updatedAt: '2026-10-17T00:00:00.000Z',
      assetCount: 0,
      shared: false,
      hasSharedLink: false,
      isActivityEnabled: false,
      albumUsers: [],
      assets: [],
      order: 'desc',
    };
    // The grid measures itself; jsdom has no ResizeObserver.
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    );
    const { calls } = mockCore([
      { method: 'GET', path: '/api/albums', answer: { json: [album] } },
      { method: 'GET', path: /^\/api\/timeline\/buckets/, answer: { json: [] } },
      { method: 'POST', path: '/api/import', answer: { status: 202, json: job({ id: 4, albumId: '5', total: 1 }) } },
    ]);

    const rootRoute = createRootRoute({ component: () => <Outlet /> });
    const app = createRoute({ getParentRoute: () => rootRoute, id: 'app', component: () => <Outlet /> });
    const page = createRoute({ getParentRoute: () => app, path: '/albums/$albumId', component: AlbumDetailPage });
    const listRoute = createRoute({ getParentRoute: () => app, path: '/albums', component: () => null });
    const router = createRouter({
      routeTree: rootRoute.addChildren([app.addChildren([page, listRoute])]),
      history: createMemoryHistory({ initialEntries: ['/albums/5'] }),
    });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <ImportProvider>
          <RouterProvider router={router} />
        </ImportProvider>
      </QueryClientProvider>,
    );

    expect(await screen.findByText(/This album is empty/)).toBeInTheDocument();
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Add pictures' })).toHaveLength(2);
    expect(screen.getAllByRole('button', { name: 'Add folder' })).toHaveLength(2);
    expect(document.querySelector('[data-drop-album-id="5"][data-drop-album-name="A"]')).not.toBeNull();

    // Outside Tauri, Add folder asks for a path and imports it into this album.
    vi.spyOn(window, 'prompt').mockReturnValue('C:\\Users\\me\\Holiday');
    fireEvent.click(screen.getAllByRole('button', { name: 'Add folder' })[0]!);
    await waitFor(() =>
      expect(calls.find((call) => call.method === 'POST' && call.path === '/api/import')?.body).toEqual({
        paths: ['C:\\Users\\me\\Holiday'],
        albumId: '5',
      }),
    );
  });
});
