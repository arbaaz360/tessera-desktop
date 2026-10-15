import { invoke } from '@tauri-apps/api/core';
import { isTauri } from './core';
import { apiJson } from './tessera';
import type { LicenceState } from './types';

/**
 * The shell's commands (contract §5), each with a dialog-free fallback for
 * the browser.
 *
 * In Tauri these reach Rust through `invoke`. In a browser there is no shell,
 * so each returns the plainest honest answer: no folder dialog (the page
 * shows a text field instead), no keychain (the licence state comes from the
 * core alone), a new tab instead of the system browser. The fallbacks exist
 * so `pnpm dev` against the mock exercises every page, not so the browser
 * build is a product.
 */

export interface ShellLicence {
  state: LicenceState;
  /** "XXXX-…-1234" or null when no key is stored. */
  keyMasked: string | null;
  checkedAt: string | null;
}

export type LicenceActivationError = 'invalid' | 'offline';

export interface LicenceActivation {
  state: LicenceState;
  error: LicenceActivationError | null;
}

/** The system folder picker. Null when the person cancelled, or outside Tauri. */
export async function pickFolder(): Promise<string | null> {
  if (!isTauri()) return null;
  const path = await invoke<string | null>('pick_folder');
  return typeof path === 'string' && path.length > 0 ? path : null;
}

export async function licenceGet(): Promise<ShellLicence> {
  if (!isTauri()) return { state: 'free', keyMasked: null, checkedAt: null };
  return invoke<ShellLicence>('licence_get');
}

/**
 * Verify a key with Gumroad, store it, and tell the core (all in the shell).
 *
 * The browser fallback skips verification - there is nothing to verify
 * against - and sets the core's state directly so the unlock flow can be
 * walked end to end against the mock. A short key is refused so the
 * "invalid" branch can be seen too.
 */
export async function licenceActivate(key: string): Promise<LicenceActivation> {
  if (isTauri()) return invoke<LicenceActivation>('licence_activate', { key });

  if (key.trim().length < 8) return { state: 'free', error: 'invalid' };
  try {
    await apiJson<void>('/api/licence', { method: 'PUT', body: JSON.stringify({ state: 'licensed' }) });
    return { state: 'licensed', error: null };
  } catch {
    return { state: 'free', error: 'offline' };
  }
}

/** The system browser. The one way a URL leaves the app. */
export async function openExternal(url: string): Promise<void> {
  if (isTauri()) {
    await invoke('open_external', { url });
    return;
  }
  window.open(url, '_blank', 'noopener,noreferrer');
}

export async function appVersion(): Promise<string> {
  if (!isTauri()) return 'dev';
  return invoke<string>('app_version');
}
