/**
 * Where the core is, and how every request proves who it is.
 *
 * The desktop core is a separate process on a loopback port the shell picks at
 * launch, guarded by a per-launch token (contract §1 and §2). The app learns
 * both once at boot - from the shell in Tauri, from `.env.local` in the
 * browser (see `core.ts`) - and `configureApi` records them here. From then on
 * this module answers two questions for the rest of the app:
 *
 *   - `apiUrl(path)`      where does `/api/...` live right now
 *   - `sessionHeaders()`  what header proves the request is ours
 *
 * Images and videos cannot send headers, so their URLs carry the same token
 * as `?sessionKey=` (contract §2). The core accepts either. A token in a URL
 * appears in the core's own logs on the user's own disk, which is the trade
 * the contract makes knowingly.
 *
 * Nothing outside `shared/api` reads the token. Features ask for URLs and
 * headers; they never see the credential.
 */

export interface CoreInfo {
  /** `http://127.0.0.1:N`, no trailing slash. */
  baseUrl: string;
  token: string;
}

let core: CoreInfo = { baseUrl: '', token: '' };

/** Called once at boot, before the first request. Safe to call again. */
export function configureApi(info: CoreInfo): void {
  core = { baseUrl: info.baseUrl.replace(/\/+$/, ''), token: info.token };
}

/** The configured core, for the boot screen and tests. */
export function getCoreInfo(): CoreInfo {
  return core;
}

/** Prefix an app-relative path (`/api/...`, `/health/...`) with the core's origin. */
export function apiUrl(path: string): string {
  return `${core.baseUrl}${path}`;
}

/** The header every hand-written request carries. Empty before boot. */
export function sessionHeaders(): Record<string, string> {
  return core.token ? { Authorization: `Bearer ${core.token}` } : {};
}

function withSessionKey(url: string): string {
  if (!core.token) return url;
  const separator = url.includes('?') ? '&' : '?';
  return `${url}${separator}sessionKey=${encodeURIComponent(core.token)}`;
}

/**
 * Media URLs, returned as plain strings rather than fetched, so the browser's
 * own image pipeline handles caching, decoding and lazy loading.
 *
 * Two sizes exist and no third: the grid thumbnail and the original. The core
 * derives the thumbnail path from the file's hash, so these URLs stay valid
 * when a file moves.
 */
export function assetThumbnailUrl(assetId: string): string {
  return withSessionKey(apiUrl(`/api/assets/${encodeURIComponent(assetId)}/thumbnail`));
}

export function assetOriginalUrl(assetId: string): string {
  return withSessionKey(apiUrl(`/api/assets/${encodeURIComponent(assetId)}/original`));
}

/** Videos play from the original: the core serves it with range requests. */
export function assetVideoUrl(assetId: string): string {
  return assetOriginalUrl(assetId);
}
