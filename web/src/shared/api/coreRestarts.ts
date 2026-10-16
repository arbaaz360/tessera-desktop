import { useEffect, useRef, useState } from 'react';
import { listen } from '@tauri-apps/api/event';
import { configureApi, getCoreInfo, type CoreInfo } from './client';
import { isTauri, rememberCore } from './core';

/**
 * Follow the core when the shell restarts it.
 *
 * The shell restarts a core that exits unexpectedly (up to three times a
 * minute), and every start picks a new port and mints a new token. It says
 * so with the `core-ready` event, payload `{ baseUrl, token }`. Before this
 * hook the app kept the address it resolved at boot, so after one crash every
 * request went to a dead port: the grid showed placeholders for ever and the
 * Library page said "Failed to fetch" until the person restarted the app.
 * Seen in the Windows UI test, 2026-10-16.
 *
 * On a changed address the hook reconfigures the API layer and bumps a
 * generation number. The caller keys the app on it, so every component
 * renders again with the new origin and token (image URLs are computed in
 * render, so refetching data alone would leave them pointing at the old
 * port), and `onRestart` lets it refetch what failed meanwhile.
 *
 * The event also fires for the first start; an address equal to the current
 * one changes nothing.
 */
export function useFollowCoreRestarts(active: boolean, onRestart?: () => void): number {
  const [generation, setGeneration] = useState(0);
  const callback = useRef(onRestart);
  callback.current = onRestart;

  useEffect(() => {
    if (!active || !isTauri()) return;

    let cancelled = false;
    let unlisten: (() => void) | null = null;

    void listen<unknown>('core-ready', (event) => {
      const next = parseCoreInfo(event.payload);
      if (!next || sameCore(next, getCoreInfo())) return;
      configureApi(next);
      rememberCore(next);
      setGeneration((current) => current + 1);
      callback.current?.();
    }).then((stop) => {
      if (cancelled) stop();
      else unlisten = stop;
    });

    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, [active]);

  return generation;
}

function parseCoreInfo(payload: unknown): CoreInfo | null {
  if (!payload || typeof payload !== 'object') return null;
  const { baseUrl, token } = payload as Partial<Record<keyof CoreInfo, unknown>>;
  if (typeof baseUrl !== 'string' || typeof token !== 'string' || !baseUrl || !token) return null;
  return { baseUrl, token };
}

function sameCore(a: CoreInfo, b: CoreInfo): boolean {
  return a.baseUrl.replace(/\/+$/, '') === b.baseUrl.replace(/\/+$/, '') && a.token === b.token;
}
