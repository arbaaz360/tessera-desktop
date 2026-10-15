import { invoke } from '@tauri-apps/api/core';
import type { CoreInfo } from './client';

/**
 * Where the core is, resolved once.
 *
 * Two worlds, one answer:
 *
 *   - IN TAURI the shell started the core, picked its port and minted its
 *     token (contract §1). The web app asks the shell through `core_info`
 *     (contract §5). `window.__TAURI_INTERNALS__` is how the app knows it is
 *     inside Tauri at all.
 *   - IN A BROWSER (Vite dev, tests) there is no shell. `.env.local` names a
 *     core - usually the mock in `web/mock/` - through `VITE_CORE_URL` and
 *     `VITE_CORE_TOKEN`.
 *
 * Resolved exactly once per page load and memoised as a promise, so a dozen
 * callers racing at boot share one `invoke`. Nothing here is a React hook:
 * the boot screen awaits it before the router exists.
 */

export class CoreUnavailableError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'CoreUnavailableError';
  }
}

export function isTauri(): boolean {
  return typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window;
}

let resolved: Promise<CoreInfo> | null = null;

export function resolveCore(): Promise<CoreInfo> {
  if (!resolved) {
    resolved = resolveOnce().catch((error: unknown) => {
      // A failed resolution must not be cached: the retry button on the boot
      // screen asks again, and the shell may have the answer by then.
      resolved = null;
      throw error;
    });
  }
  return resolved;
}

async function resolveOnce(): Promise<CoreInfo> {
  if (isTauri()) {
    const info = await invoke<Partial<CoreInfo>>('core_info');
    if (!info || typeof info.baseUrl !== 'string' || typeof info.token !== 'string') {
      throw new CoreUnavailableError('The shell answered core_info without a baseUrl and token.');
    }
    return { baseUrl: info.baseUrl, token: info.token };
  }

  const baseUrl = import.meta.env.VITE_CORE_URL as string | undefined;
  const token = import.meta.env.VITE_CORE_TOKEN as string | undefined;
  if (!baseUrl || !token) {
    throw new CoreUnavailableError(
      'No core configured. Outside Tauri the app reads VITE_CORE_URL and VITE_CORE_TOKEN from web/.env.local; copy .env.example and run `pnpm mock`.',
    );
  }
  return { baseUrl, token };
}

/** Tests only: forget the memoised answer. */
export function resetCoreForTests(): void {
  resolved = null;
}

/**
 * `GET /health/ready` → 200 once the database is migrated and the workers run
 * (contract §1). No token. A core that is up but not ready, or not up at all,
 * both answer "not yet"; the boot screen does not need to tell them apart.
 */
export async function isCoreReady(info: CoreInfo): Promise<boolean> {
  try {
    const response = await fetch(`${info.baseUrl}/health/ready`, { cache: 'no-store' });
    return response.status === 200;
  } catch {
    return false;
  }
}
