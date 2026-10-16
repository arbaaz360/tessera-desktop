import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { mockCore, renderPage } from '@/test/render';
import type { DuplicateSet, Library } from '@/shared/api/types';
import { LibraryPage } from './LibraryPage';

/**
 * The Library page is numbers. The tests check that the core's numbers reach
 * the screen as they are - counts, last-scan figures, the cap - and that the
 * two actions with consequences do what they say: Scan now posts to the
 * root's scan route, Remove warns that files are untouched and then deletes
 * the root.
 */

const library: Library = {
  roots: [
    {
      id: 1,
      path: 'C:\\Users\\me\\References',
      label: 'References',
      enabled: true,
      counts: { files: 12034, contents: 11870, trashed: 3 },
      lastScan: {
        startedAt: '2026-10-16T09:00:00.000Z',
        finishedAt: '2026-10-16T09:04:00.000Z',
        seen: 12034,
        added: 10,
        moved: 2,
        gone: 0,
        errors: 0,
      },
      scanning: false,
    },
  ],
  thumbnails: { pending: 120, done: 11750 },
  embeddings: { pending: 300, done: 11570, model: 'siglip2-base-patch16-256', ready: true },
  licence: { state: 'free', cap: 2000, indexed: 11870, capReached: true },
  watch: { running: true, lastTick: '2026-10-16T09:10:00.000Z' },
};

const duplicates: DuplicateSet[] = [
  {
    contentId: 42,
    sha256: 'abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789',
    byteSize: 2_400_000,
    locations: [
      { id: 1, rootId: 1, relPath: 'sketches/horse.jpg' },
      { id: 2, rootId: 1, relPath: 'copies/horse.jpg' },
    ],
  },
];

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('LibraryPage', () => {
  it('shows the roots with their counts, the queues, the cap banner and the duplicates', async () => {
    mockCore([
      { method: 'GET', path: '/api/library', answer: { json: library } },
      { method: 'GET', path: /^\/api\/library\/duplicates/, answer: { json: duplicates } },
    ]);

    renderPage(<LibraryPage />, '/library');

    await waitFor(() => expect(screen.getByText('C:\\Users\\me\\References')).toBeInTheDocument());

    // Counts, formatted, next to their labels.
    const folders = screen.getByRole('region', { name: 'Folders' });
    expect(within(folders).getByText('12,034')).toBeInTheDocument();
    expect(within(folders).getByText('11,870')).toBeInTheDocument();
    expect(within(folders).getByText(/12,034 files seen/)).toBeInTheDocument();

    // The queues, as counts not ticks. The library is past the free cap, so
    // the 120 thumbnails still to make are held by it, not pending.
    expect(
      screen.getByRole('progressbar', { name: '11,750 done · 0 pending · 120 held by the free tier' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('progressbar', { name: '11,570 done · 300 pending · siglip2-base-patch16-256' }),
    ).toBeInTheDocument();

    // The cap, with the way out.
    const banner = screen.getByRole('status');
    expect(banner).toHaveTextContent('11,870 pictures found, 2,000 indexed');
    expect(within(banner).getByRole('link', { name: 'Unlock' })).toHaveAttribute('href', '/settings#licence');

    // Duplicates: both paths of the one picture.
    await waitFor(() => expect(screen.getByText('sketches/horse.jpg')).toBeInTheDocument());
    expect(screen.getByText('copies/horse.jpg')).toBeInTheDocument();
    expect(screen.getByText(/1 picture in more than one place/)).toBeInTheDocument();
  });

  it('Scan now posts to the root\'s scan route', async () => {
    const { calls } = mockCore([
      { method: 'GET', path: '/api/library', answer: { json: library } },
      { method: 'GET', path: /^\/api\/library\/duplicates/, answer: { json: [] } },
      { method: 'POST', path: '/api/library/roots/1/scan', answer: { status: 202, json: { runId: 9 } } },
    ]);

    renderPage(<LibraryPage />, '/library');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Scan now' }));
    await waitFor(() =>
      expect(calls.some((call) => call.method === 'POST' && call.path === '/api/library/roots/1/scan')).toBe(true),
    );
  });

  it('Remove says the files are untouched, then deletes the root', async () => {
    const { calls } = mockCore([
      { method: 'GET', path: '/api/library', answer: { json: library } },
      { method: 'GET', path: /^\/api\/library\/duplicates/, answer: { json: [] } },
      { method: 'DELETE', path: '/api/library/roots/1', answer: { status: 204 } },
    ]);

    renderPage(<LibraryPage />, '/library');
    const user = userEvent.setup();

    await user.click(await screen.findByRole('button', { name: 'Remove' }));

    const dialog = screen.getByRole('dialog');
    expect(dialog).toHaveTextContent('The files on disk are not touched');
    expect(dialog).toHaveTextContent('11,870 pictures');
    expect(calls.some((call) => call.method === 'DELETE')).toBe(false);

    await user.click(within(dialog).getByRole('button', { name: 'Remove folder' }));
    await waitFor(() =>
      expect(calls.some((call) => call.method === 'DELETE' && call.path === '/api/library/roots/1')).toBe(true),
    );
  });
});
