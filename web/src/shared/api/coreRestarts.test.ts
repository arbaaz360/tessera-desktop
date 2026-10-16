import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';

/**
 * The shell restarts a crashed core on a new port with a new token and says
 * so with `core-ready`. In the Windows UI test the app ignored it and went
 * dead. These tests pin that the app follows a changed address, ignores the
 * same address and malformed payloads, and stops listening on unmount.
 */

type Handler = (event: { payload: unknown }) => void;
let handler: Handler | null = null;
const unlisten = vi.fn();
const listen = vi.fn(async (name: string, callback: Handler) => {
  if (name === 'core-ready') handler = callback;
  return unlisten;
});
vi.mock('@tauri-apps/api/event', () => ({ listen: (name: string, cb: Handler) => listen(name, cb) }));

const invoke = vi.fn();
vi.mock('@tauri-apps/api/core', () => ({ invoke: (...args: unknown[]) => invoke(...args) }));

const { useFollowCoreRestarts } = await import('./coreRestarts');
const { configureApi, getCoreInfo, assetThumbnailUrl } = await import('./client');
const { resolveCore, resetCoreForTests } = await import('./core');

const BOOT = { baseUrl: 'http://127.0.0.1:53405', token: 'boot-token' };
const RESTARTED = { baseUrl: 'http://127.0.0.1:61284', token: 'new-token' };

beforeEach(() => {
  handler = null;
  listen.mockClear();
  unlisten.mockClear();
  invoke.mockReset();
  resetCoreForTests();
  configureApi(BOOT);
  Object.defineProperty(window, '__TAURI_INTERNALS__', { value: {}, configurable: true });
});

afterEach(() => {
  delete (window as unknown as Record<string, unknown>)['__TAURI_INTERNALS__'];
});

describe('useFollowCoreRestarts', () => {
  it('switches the API to the restarted core and bumps the generation', async () => {
    const onRestart = vi.fn();
    const { result } = renderHook(() => useFollowCoreRestarts(true, onRestart));
    await waitFor(() => expect(handler).not.toBeNull());
    expect(result.current).toBe(0);

    act(() => handler!({ payload: RESTARTED }));

    expect(getCoreInfo()).toEqual(RESTARTED);
    expect(assetThumbnailUrl('7')).toBe('http://127.0.0.1:61284/api/assets/7/thumbnail?sessionKey=new-token');
    expect(result.current).toBe(1);
    expect(onRestart).toHaveBeenCalledTimes(1);
    // The boot screen's retry must not hand back the dead address.
    await expect(resolveCore()).resolves.toEqual(RESTARTED);
    expect(invoke).not.toHaveBeenCalled();
  });

  it('ignores the event for the core it already talks to, and payloads without an address', async () => {
    const onRestart = vi.fn();
    const { result } = renderHook(() => useFollowCoreRestarts(true, onRestart));
    await waitFor(() => expect(handler).not.toBeNull());

    act(() => handler!({ payload: { ...BOOT, baseUrl: `${BOOT.baseUrl}/` } }));
    act(() => handler!({ payload: { baseUrl: 'http://127.0.0.1:1' } }));
    act(() => handler!({ payload: null }));

    expect(getCoreInfo()).toEqual(BOOT);
    expect(result.current).toBe(0);
    expect(onRestart).not.toHaveBeenCalled();
  });

  it('does not listen before boot or outside Tauri, and stops listening on unmount', async () => {
    renderHook(() => useFollowCoreRestarts(false));
    delete (window as unknown as Record<string, unknown>)['__TAURI_INTERNALS__'];
    renderHook(() => useFollowCoreRestarts(true));
    expect(listen).not.toHaveBeenCalled();

    Object.defineProperty(window, '__TAURI_INTERNALS__', { value: {}, configurable: true });
    const { unmount } = renderHook(() => useFollowCoreRestarts(true));
    await waitFor(() => expect(listen).toHaveBeenCalledTimes(1));
    unmount();
    await waitFor(() => expect(unlisten).toHaveBeenCalledTimes(1));
  });
});
