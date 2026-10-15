import { setBaseUrl } from '@immich/sdk';

/**
 * API client configuration.
 *
 * The generated `@immich/sdk` is the single source of truth for request and
 * response types. Hand-writing them would mean maintaining a second description
 * of the same API that silently drifts from the first — the exact failure this
 * project exists to stop repeating.
 *
 * Auth is a session cookie set by the login endpoint. We do NOT call
 * `setApiKey`: an API key held in JavaScript is readable by anything else on
 * the page. Because the dev server proxies `/api` to Immich, every request is
 * same-origin, and the browser attaches the session cookie automatically —
 * no configuration required.
 */

const BASE_URL = '/api';

let configured = false;

export function configureApi(): void {
  if (configured) return;
  setBaseUrl(BASE_URL);
  configured = true;
}

/**
 * Build a thumbnail URL.
 *
 * Returned as a plain URL rather than fetched, so the browser's own image
 * pipeline handles caching, decoding and lazy loading. Fetching these through
 * JS would mean reimplementing all three, worse.
 *
 * There are two sizes in this library and no third: the grid thumbnail and
 * the original (the owner's rule; most originals are 1080-1440 px anyway).
 * Anything full-screen uses `assetOriginalUrl`.
 */

/**
 * The session token that image URLs carry, when account switching is active.
 *
 * `<img>` and `<video>` cannot send an Authorization header, so on their own
 * they authenticate with the session COOKIE — which belongs to whichever
 * account logged in through the form most recently, not the account the app
 * switched to. The server resolves `?sessionKey=<token>` BEFORE the cookie
 * (verified in the fork's auth.service.ts), so appending the active account's
 * token here keeps image bytes and API data answering as the same identity.
 *
 * The auth feature owns tokens and calls `setUrlSessionKey` whenever the
 * active account changes; this module only holds the current value. That keeps
 * the dependency pointing feature → shared, never the reverse. A token in a
 * URL does appear in server access logs — accepted on this LAN-only
 * deployment, the same trade-off as the localStorage token store itself.
 */
let urlSessionKey: string | null = null;

export function setUrlSessionKey(token: string | null): void {
  urlSessionKey = token;
}

/**
 * The active account's credentials, for the requests this app writes by hand.
 *
 * There are three ways a request leaves this app: the generated SDK (which
 * reads `defaults.headers`), image/video URLs (which carry `?sessionKey=`), and
 * the hand-written `fetch` helpers for the timeline and for Tessera's own
 * endpoints. The auth feature kept the first two in lockstep and missed the
 * third, so with two accounts signed in those helpers answered as the COOKIE's
 * account - whichever logged in last - while everything else answered as the
 * active one. Opening an album of the active account asked the timeline as the
 * other account and got an empty grid; search, the overlap badge and album
 * moves went the same way (found 2026-10-05). Every hand-written request takes
 * its headers from here, so the third path cannot drift from the other two.
 *
 * Empty when no account switch is active: the cookie is then the only session
 * and is the right one.
 */
export function sessionHeaders(): Record<string, string> {
  return urlSessionKey ? { Authorization: `Bearer ${urlSessionKey}` } : {};
}

function withSessionKey(url: string): string {
  if (!urlSessionKey) return url;
  const separator = url.includes('?') ? '&' : '?';
  return `${url}${separator}sessionKey=${encodeURIComponent(urlSessionKey)}`;
}

export function assetThumbnailUrl(assetId: string): string {
  return withSessionKey(`${BASE_URL}/assets/${assetId}/thumbnail`);
}

export function assetOriginalUrl(assetId: string): string {
  return withSessionKey(`${BASE_URL}/assets/${assetId}/original`);
}

export function assetVideoUrl(assetId: string): string {
  return withSessionKey(`${BASE_URL}/assets/${assetId}/video/playback`);
}
