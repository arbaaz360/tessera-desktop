import { apiUrl, sessionHeaders } from './client';

/**
 * The one way JSON leaves and enters this app.
 *
 * Every request goes through here so three things happen in exactly one
 * place: the path is resolved against the core's origin, the token rides
 * along as a bearer header, and a non-2xx answer becomes an `ApiError` that
 * carries the status and the core's own `detail`. TanStack Query then
 * surfaces that like any other failure, and a page that wants to react to a
 * particular status (search's 503 "model not ready") can read it off the
 * error instead of parsing a message.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly detail: string;

  constructor(status: number, detail: string) {
    super(`${status} ${detail}`);
    this.name = 'ApiError';
    this.status = status;
    this.detail = detail;
  }
}

export async function apiJson<T>(path: string, init?: RequestInit): Promise<T> {
  // The token rides along with whatever headers the caller set. A caller's
  // own Authorization wins, which is what lets a test pin a different one.
  const headers = new Headers(init?.headers);
  for (const [name, value] of Object.entries(sessionHeaders())) {
    if (!headers.has(name)) headers.set(name, value);
  }
  if (init?.body !== undefined && !headers.has('content-type')) {
    headers.set('content-type', 'application/json');
  }

  const response = await fetch(apiUrl(path), { ...init, headers });
  if (!response.ok) {
    throw new ApiError(response.status, await readDetail(response));
  }
  // A 204, a 202 "started" or any other answer without a body is undefined,
  // not a JSON parse error: the core's accept-and-carry-on routes send none.
  const text = await response.text();
  if (text.length === 0) return undefined as T;
  return JSON.parse(text) as T;
}

/** The core says why in `detail` (contract §4); older routes said it in `title`. */
async function readDetail(response: Response): Promise<string> {
  try {
    const problem = (await response.json()) as { title?: string; detail?: string; message?: string };
    return problem.detail ?? problem.title ?? problem.message ?? response.statusText;
  } catch {
    return response.statusText || `HTTP ${response.status}`;
  }
}

/** One place that knows how to read an error for a person. */
export function apiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.detail;
  return error instanceof Error ? error.message : 'Something went wrong.';
}

/** Is this the core saying "model not ready" (contract §4: 503)? */
export function isModelNotReady(error: unknown): boolean {
  return error instanceof ApiError && error.status === 503;
}
