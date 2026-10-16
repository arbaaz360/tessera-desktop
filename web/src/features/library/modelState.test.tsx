import { afterEach, describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import { mockCore, renderPage } from '@/test/render';
import type { Library, SearchStatus } from '@/shared/api/types';
import { libraryIsBusy, searchIsBusy, thumbnailsHeldByCap } from './api';
import { modelState } from './modelState';
import { SearchModelProgress } from './SearchModelProgress';

/**
 * The payloads below are the real core's answers from the Windows UI test
 * (2026-10-16), copied as they came: the core leaves null fields out.
 */

/** 50 seconds after a restart: model on disk, being loaded. */
const loadingAfterRestart: SearchStatus = {
  model: 'siglip2-base-patch16-256',
  ready: false,
  embedded: 28,
  total: 28,
  pending: 0,
  failed: 0,
  enabled: true,
  device: 'cpu',
  waiting: 'model not ready',
};

/** Ready and idle: no `downloading` key at all. */
const readyIdle: SearchStatus = {
  model: 'siglip2-base-patch16-256',
  ready: true,
  embedded: 2000,
  total: 2000,
  pending: 0,
  failed: 0,
  enabled: true,
  device: 'directml',
};

/** 2,128 pictures on the free tier: 128 thumbnails held back by the cap. */
const capped: Library = {
  roots: [],
  thumbnails: { pending: 128, done: 2000 },
  embeddings: { pending: 0, done: 2000, model: 'siglip2-base-patch16-256', ready: true },
  licence: { state: 'free', cap: 2000, indexed: 2128, capReached: true },
  watch: { running: true, lastTick: '2026-10-16T08:43:13.949Z' },
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('modelState', () => {
  it('calls a model that is on disk but not loaded yet "loading", not "not downloaded"', () => {
    expect(modelState(loadingAfterRestart)).toBe('loading');
  });

  it('tells downloading, failed, indexing, ready and off apart', () => {
    const base = { ...loadingAfterRestart };
    expect(modelState({ ...base, downloading: { receivedBytes: 1, totalBytes: 2, file: 'text.onnx' } })).toBe(
      'downloading',
    );
    expect(modelState({ ...base, error: 'No such host is known.' })).toBe('failed');
    expect(modelState({ ...readyIdle, embedded: 10 })).toBe('indexing');
    expect(modelState(readyIdle)).toBe('ready');
    expect(modelState({ ...readyIdle, enabled: false })).toBe('off');
  });
});

describe('polling', () => {
  it('does not poll the search status as busy when ready and the core omits `downloading`', () => {
    expect(searchIsBusy(readyIdle)).toBe(false);
    expect(searchIsBusy(loadingAfterRestart)).toBe(true);
  });

  it('does not count thumbnails held by the free tier as work in progress', () => {
    expect(thumbnailsHeldByCap(capped)).toBe(128);
    expect(libraryIsBusy(capped)).toBe(false);
    expect(libraryIsBusy({ ...capped, thumbnails: { pending: 200, done: 1928 } })).toBe(true);
    expect(thumbnailsHeldByCap({ ...capped, licence: { ...capped.licence, state: 'licensed' } })).toBe(0);
  });
});

describe('SearchModelProgress', () => {
  it('while the model loads: says so, offers no download, and does not claim the CPU', async () => {
    mockCore([{ method: 'GET', path: '/api/search/status', answer: { json: loadingAfterRestart } }]);
    renderPage(<SearchModelProgress />);

    expect(await screen.findByText(/· loading/)).toBeInTheDocument();
    expect(screen.getByText(/Getting the model ready on this machine/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /download/i })).not.toBeInTheDocument();
    expect(screen.queryByText(/runs on cpu/)).not.toBeInTheDocument();
  });

  it('after a failed download: shows the reason and the real size', async () => {
    mockCore([
      {
        method: 'GET',
        path: '/api/search/status',
        answer: { json: { ...loadingAfterRestart, error: 'No such host is known.' } },
      },
    ]);
    renderPage(<SearchModelProgress />);

    expect(await screen.findByText(/No such host is known/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try the download again' })).toBeInTheDocument();
    expect(screen.getByText(/About 755 MB/)).toBeInTheDocument();
  });
});
