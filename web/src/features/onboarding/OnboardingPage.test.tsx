import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { mockCore, renderPage } from '@/test/render';
import type { Library, LibraryRoot, SearchStatus } from '@/shared/api/types';
import { OnboardingPage } from './OnboardingPage';

/**
 * Onboarding outside Tauri: the path field stands in for the folder picker.
 * What matters is the sequence the contract asks for - POST the root, start a
 * scan, then show the model's progress - and that the core's refusals come
 * through in its own words.
 */

const emptyLibrary: Library = {
  roots: [],
  thumbnails: { pending: 0, done: 0 },
  embeddings: { pending: 0, done: 0, model: 'siglip2-base-patch16-256', ready: false },
  licence: { state: 'free', cap: 2000, indexed: 0, capReached: false },
  watch: { running: false, lastTick: null },
};

const newRoot: LibraryRoot = {
  id: 7,
  path: 'C:\\Users\\me\\References',
  label: 'References',
  enabled: true,
  counts: { files: 0, contents: 0, trashed: 0 },
  lastScan: null,
  scanning: true,
};

const downloading: SearchStatus = {
  model: 'siglip2-base-patch16-256',
  ready: false,
  downloading: { receivedBytes: 50 * 1024 * 1024, totalBytes: 190 * 1024 * 1024, file: 'vision.onnx' },
  embedded: 0,
  total: 0,
  enabled: true,
  device: 'cpu',
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('OnboardingPage', () => {
  it('adds the folder, starts a scan and shows the model download', async () => {
    let library = emptyLibrary;
    const { calls } = mockCore([
      { method: 'GET', path: '/api/library', answer: () => ({ json: library }) },
      {
        method: 'POST',
        path: '/api/library/roots',
        answer: () => {
          library = { ...emptyLibrary, roots: [{ ...newRoot, counts: { files: 120, contents: 118, trashed: 0 } }] };
          return { status: 201, json: newRoot };
        },
      },
      { method: 'POST', path: '/api/library/roots/7/scan', answer: { status: 202, json: { runId: 1 } } },
      { method: 'GET', path: '/api/search/status', answer: { json: downloading } },
    ]);

    renderPage(<OnboardingPage />, '/welcome');
    const user = userEvent.setup();

    // The router mounts its route asynchronously; the first query waits.
    expect(await screen.findByText(/Your folders stay yours/)).toBeInTheDocument();

    await user.type(screen.getByLabelText('Folder path'), 'C:\\Users\\me\\References');
    await user.click(screen.getByRole('button', { name: 'Add folder' }));

    await waitFor(() => expect(screen.getByText('Reading your folder')).toBeInTheDocument());

    const post = calls.find((call) => call.method === 'POST' && call.path === '/api/library/roots');
    expect(post?.body).toEqual({ path: 'C:\\Users\\me\\References' });
    expect(post?.headers.get('Authorization')).toBe('Bearer test-token');
    expect(calls.some((call) => call.method === 'POST' && call.path === '/api/library/roots/7/scan')).toBe(true);

    // The new root, with its live count, and the download in bytes.
    await waitFor(() => expect(screen.getByText(/120 files seen/)).toBeInTheDocument());
    expect(screen.getByRole('progressbar', { name: /50\.0 MB of 190 MB/ })).toBeInTheDocument();
  });

  it("shows the core's reason when a path is refused", async () => {
    mockCore([
      { method: 'GET', path: '/api/library', answer: { json: emptyLibrary } },
      {
        method: 'POST',
        path: '/api/library/roots',
        answer: { status: 400, json: { detail: 'C:\\nope is not a directory' } },
      },
      { method: 'GET', path: '/api/search/status', answer: { json: downloading } },
    ]);

    renderPage(<OnboardingPage />, '/welcome');
    const user = userEvent.setup();

    await user.type(await screen.findByLabelText('Folder path'), 'C:\\nope');
    await user.click(screen.getByRole('button', { name: 'Add folder' }));

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('C:\\nope is not a directory'));
    expect(screen.queryByText('Reading your folder')).not.toBeInTheDocument();
  });
});
