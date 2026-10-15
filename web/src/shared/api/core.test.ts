import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * `resolveCore` has two sources and one promise. The tests pin: which source
 * answers in which world, that a missing browser config is a named error,
 * that the Tauri answer is asked for once however many callers race, and
 * that a failure is not cached.
 */

const invoke = vi.fn();
vi.mock('@tauri-apps/api/core', () => ({ invoke: (...args: unknown[]) => invoke(...args) }));

// Imported after the mock so the module picks up the stub.
const { resolveCore, resetCoreForTests, isCoreReady, isTauri, CoreUnavailableError } = await import('./core');

function enterTauri() {
  Object.defineProperty(window, '__TAURI_INTERNALS__', { value: {}, configurable: true });
}
function leaveTauri() {
  delete (window as unknown as Record<string, unknown>)['__TAURI_INTERNALS__'];
}

beforeEach(() => {
  resetCoreForTests();
  invoke.mockReset();
  leaveTauri();
  vi.unstubAllEnvs();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('resolveCore outside Tauri', () => {
  it('reads VITE_CORE_URL and VITE_CORE_TOKEN', async () => {
    vi.stubEnv('VITE_CORE_URL', 'http://127.0.0.1:2501');
    vi.stubEnv('VITE_CORE_TOKEN', 'dev-token');
    expect(isTauri()).toBe(false);
    await expect(resolveCore()).resolves.toEqual({ baseUrl: 'http://127.0.0.1:2501', token: 'dev-token' });
    expect(invoke).not.toHaveBeenCalled();
  });

  it('throws a CoreUnavailableError when the env is missing, and does not cache the failure', async () => {
    vi.stubEnv('VITE_CORE_URL', '');
    vi.stubEnv('VITE_CORE_TOKEN', '');
    await expect(resolveCore()).rejects.toBeInstanceOf(CoreUnavailableError);

    vi.stubEnv('VITE_CORE_URL', 'http://127.0.0.1:2501');
    vi.stubEnv('VITE_CORE_TOKEN', 'dev-token');
    await expect(resolveCore()).resolves.toEqual({ baseUrl: 'http://127.0.0.1:2501', token: 'dev-token' });
  });
});

describe('resolveCore in Tauri', () => {
  it("asks the shell for core_info and ignores the env", async () => {
    enterTauri();
    vi.stubEnv('VITE_CORE_URL', 'http://should-not-be-used');
    invoke.mockResolvedValue({ baseUrl: 'http://127.0.0.1:41234', token: 'shell-token' });

    expect(isTauri()).toBe(true);
    await expect(resolveCore()).resolves.toEqual({ baseUrl: 'http://127.0.0.1:41234', token: 'shell-token' });
    expect(invoke).toHaveBeenCalledWith('core_info');
  });

  it('resolves once however many callers race', async () => {
    enterTauri();
    invoke.mockResolvedValue({ baseUrl: 'http://127.0.0.1:1', token: 't' });
    await Promise.all([resolveCore(), resolveCore(), resolveCore()]);
    expect(invoke).toHaveBeenCalledTimes(1);
  });

  it('rejects an answer without a baseUrl and token', async () => {
    enterTauri();
    invoke.mockResolvedValue({ port: 1234 });
    await expect(resolveCore()).rejects.toBeInstanceOf(CoreUnavailableError);
  });
});

describe('isCoreReady', () => {
  it('is true only for a 200 from /health/ready, and false when the core is unreachable', async () => {
    const fetchMock = vi.fn(async (url: string) => {
      if (url.endsWith('/health/ready')) return new Response('', { status: 200 });
      return new Response('', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);
    await expect(isCoreReady({ baseUrl: 'http://core.test', token: 't' })).resolves.toBe(true);
    expect(fetchMock.mock.calls[0]?.[0]).toBe('http://core.test/health/ready');

    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 503 })));
    await expect(isCoreReady({ baseUrl: 'http://core.test', token: 't' })).resolves.toBe(false);

    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new TypeError('connection refused'))));
    await expect(isCoreReady({ baseUrl: 'http://core.test', token: 't' })).resolves.toBe(false);
  });
});
