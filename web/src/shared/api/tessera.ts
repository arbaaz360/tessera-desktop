import { sessionHeaders } from './client';

/**
 * Tessera's own endpoints - the ones with no Immich equivalent.
 *
 * The generated `@immich/sdk` covers the Immich-shaped surface (timeline,
 * albums, assets, users). Everything Tessera adds beyond that - cover
 * candidates, workers, search - is same-origin under the same `/api`, carries
 * the same session cookie, and is reached through this one wrapper so the
 * error handling lives in one place: a non-2xx becomes a thrown Error carrying
 * the status and the problem title, which TanStack Query then surfaces like
 * any other failure.
 *
 * Same origin is the whole reason no address table is needed here. The app is
 * served by Tessera; `/api/...` is Tessera wherever the app was opened from.
 */
export async function apiJson<T>(path: string, init?: RequestInit): Promise<T> {
  // The active account's token rides along with whatever headers the caller
  // set (a caller's own Authorization wins), so these endpoints answer as the
  // same account as the rest of the app: see sessionHeaders.
  const headers = new Headers(init?.headers);
  for (const [name, value] of Object.entries(sessionHeaders())) {
    if (!headers.has(name)) headers.set(name, value);
  }
  const response = await fetch(path, { credentials: 'same-origin', ...init, headers });
  if (!response.ok) {
    let title = response.statusText;
    try {
      const problem = (await response.json()) as { title?: string; detail?: string };
      title = problem.detail ?? problem.title ?? title;
    } catch {
      /* not a problem document; the status text will do */
    }
    throw new Error(`${response.status} ${title}`);
  }
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}
