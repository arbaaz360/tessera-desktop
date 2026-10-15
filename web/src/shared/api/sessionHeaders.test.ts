import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { sessionHeaders, setUrlSessionKey } from './client';
import { apiJson } from './tessera';
import { getTimeBuckets } from './timeline';

/**
 * With two accounts signed in, the session COOKIE belongs to whichever account
 * logged in last, and the active account is carried by a token. The SDK and the
 * image URLs sent that token; the hand-written fetch helpers did not, so the
 * timeline and Tessera's own endpoints answered as the other account - an album
 * of the active account opened as an empty grid (2026-10-05). These pin the
 * third path to the same token.
 */
describe('hand-written requests carry the active account', () => {
  const calls: Array<{ url: string; init: RequestInit | undefined }> = [];

  beforeEach(() => {
    calls.length = 0;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) => {
        calls.push({ url, init });
        return new Response('[]', { status: 200, headers: { 'content-type': 'application/json' } });
      }),
    );
  });

  afterEach(() => {
    setUrlSessionKey(null);
    vi.unstubAllGlobals();
  });

  const authOf = (init: RequestInit | undefined) => new Headers(init?.headers).get('Authorization');

  it('sessionHeaders is empty without an account switch and a bearer with one', () => {
    expect(sessionHeaders()).toEqual({});
    setUrlSessionKey('token-foreign');
    expect(sessionHeaders()).toEqual({ Authorization: 'Bearer token-foreign' });
  });

  it('the timeline (album grids, the main grid) sends the active token', async () => {
    setUrlSessionKey('token-foreign');
    await getTimeBuckets({ albumId: 'album-1' });
    expect(calls[0]?.url).toContain('/api/timeline/buckets');
    expect(authOf(calls[0]?.init)).toBe('Bearer token-foreign');
  });

  it("Tessera's own endpoints send it too, keeping the caller's other headers", async () => {
    setUrlSessionKey('token-foreign');
    await apiJson('/api/albums/overlap', { headers: { 'content-type': 'application/json' } });
    const headers = new Headers(calls[0]?.init?.headers);
    expect(headers.get('Authorization')).toBe('Bearer token-foreign');
    expect(headers.get('content-type')).toBe('application/json');
  });

  it("a caller's own Authorization is not overwritten", async () => {
    setUrlSessionKey('token-foreign');
    await apiJson('/api/x', { headers: { Authorization: 'Bearer explicit' } });
    expect(authOf(calls[0]?.init)).toBe('Bearer explicit');
  });

  it('with a single account nothing is added and the cookie decides', async () => {
    await getTimeBuckets();
    await apiJson('/api/y');
    expect(authOf(calls[0]?.init)).toBeNull();
    expect(authOf(calls[1]?.init)).toBeNull();
  });
});
