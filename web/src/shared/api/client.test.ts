import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  assetOriginalUrl,
  assetThumbnailUrl,
  assetVideoUrl,
  configureApi,
  apiUrl,
  sessionHeaders,
} from './client';
import { ApiError, apiErrorMessage, apiJson, isModelNotReady } from './tessera';
import { getTimeBuckets } from './timeline';

/**
 * Contract §2: every `/api` request carries the token as a bearer header, and
 * media URLs - which cannot send headers - carry it as `?sessionKey=`. These
 * pin both paths to the one token `configureApi` was given, and the error
 * shape the rest of the app relies on.
 */
describe('the client, once configured', () => {
  const calls: Array<{ url: string; init: RequestInit | undefined }> = [];

  beforeEach(() => {
    calls.length = 0;
    configureApi({ baseUrl: 'http://127.0.0.1:4321/', token: 'tok-123' });
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        calls.push({ url, init });
        if (url.endsWith('/api/missing')) {
          return new Response(JSON.stringify({ detail: 'no such thing' }), {
            status: 404,
            headers: { 'content-type': 'application/json' },
          });
        }
        if (url.includes('/api/search')) {
          return new Response(JSON.stringify({ detail: 'model not ready' }), {
            status: 503,
            headers: { 'content-type': 'application/json' },
          });
        }
        if (url.endsWith('/api/nothing')) return new Response(null, { status: 204 });
        return new Response('[]', { status: 200, headers: { 'content-type': 'application/json' } });
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    configureApi({ baseUrl: '', token: '' });
  });

  const authOf = (init: RequestInit | undefined) => new Headers(init?.headers).get('Authorization');

  it('resolves /api paths against the core origin, without a doubled slash', () => {
    expect(apiUrl('/api/library')).toBe('http://127.0.0.1:4321/api/library');
  });

  it('sends the bearer header on every hand-written request', async () => {
    expect(sessionHeaders()).toEqual({ Authorization: 'Bearer tok-123' });

    await apiJson('/api/library');
    await getTimeBuckets({ albumId: 'album-1' });

    expect(calls[0]?.url).toBe('http://127.0.0.1:4321/api/library');
    expect(authOf(calls[0]?.init)).toBe('Bearer tok-123');
    expect(calls[1]?.url).toBe('http://127.0.0.1:4321/api/timeline/buckets?albumId=album-1');
    expect(authOf(calls[1]?.init)).toBe('Bearer tok-123');
  });

  it('adds a JSON content type when there is a body, and keeps the caller\'s headers', async () => {
    await apiJson('/api/albums', { method: 'POST', body: JSON.stringify({ albumName: 'x' }) });
    const headers = new Headers(calls[0]?.init?.headers);
    expect(headers.get('content-type')).toBe('application/json');
    expect(headers.get('Authorization')).toBe('Bearer tok-123');

    await apiJson('/api/x', { headers: { Authorization: 'Bearer explicit' } });
    expect(authOf(calls[1]?.init)).toBe('Bearer explicit');
  });

  it('puts the same token on media URLs as sessionKey', () => {
    expect(assetThumbnailUrl('a1')).toBe('http://127.0.0.1:4321/api/assets/a1/thumbnail?sessionKey=tok-123');
    expect(assetOriginalUrl('a1')).toBe('http://127.0.0.1:4321/api/assets/a1/original?sessionKey=tok-123');
    // Videos play from the original: the only two sizes the core serves.
    expect(assetVideoUrl('a1')).toBe(assetOriginalUrl('a1'));
  });

  it('turns a non-2xx into an ApiError carrying the status and the core\'s detail', async () => {
    const error = await apiJson('/api/missing').catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(404);
    expect((error as ApiError).detail).toBe('no such thing');
    expect(apiErrorMessage(error)).toBe('no such thing');
  });

  it('recognises the search 503 as "model not ready"', async () => {
    const error = await apiJson('/api/search?q=x').catch((e: unknown) => e);
    expect(isModelNotReady(error)).toBe(true);
    expect(isModelNotReady(new Error('other'))).toBe(false);
  });

  it('returns undefined for a 204', async () => {
    await expect(apiJson<void>('/api/nothing', { method: 'DELETE' })).resolves.toBeUndefined();
  });
});

describe('before configuration', () => {
  it('sends no header and adds no sessionKey, rather than an empty one', () => {
    configureApi({ baseUrl: '', token: '' });
    expect(sessionHeaders()).toEqual({});
    expect(assetThumbnailUrl('a1')).toBe('/api/assets/a1/thumbnail');
  });
});
