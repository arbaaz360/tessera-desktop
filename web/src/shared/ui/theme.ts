/**
 * Light/dark theme selection.
 *
 * `styles/theme.css` already defines all three states — a bare `:root` dark
 * palette, a `prefers-color-scheme: light` block, and explicit
 * `[data-theme='light'|'dark']` stamps that override both. This module is only
 * the part that decides WHICH stamp goes on `<html>`, and remembers it.
 *
 * DARK IS THE DEFAULT, not "follow the OS". Without a stamp the page follows
 * `prefers-color-scheme`, which meant the same library rendered light on a Mac
 * and dark on a PC with no way to change either. For a photo viewer that is the
 * wrong default in both directions: a dark chrome lets the photographs supply
 * all the colour, which is the whole premise of the palette. "System" remains
 * available, but you have to ask for it.
 */

export type ThemeChoice = 'dark' | 'light' | 'system';

const STORAGE_KEY = 'photos-web.theme';

export function readThemeChoice(): ThemeChoice {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored === 'dark' || stored === 'light' || stored === 'system') return stored;
  } catch {
    // Private window or storage blocked — fall through to the default.
  }
  return 'dark';
}

/**
 * Put the choice on `<html>`.
 *
 * 'system' REMOVES the attribute rather than computing a value, which hands the
 * decision back to the `prefers-color-scheme` media query in CSS. That matters:
 * it means the page keeps tracking the OS live, including if the user flips
 * their OS theme while the tab is open, with no listener needed here.
 */
export function applyTheme(choice: ThemeChoice): void {
  const root = document.documentElement;
  if (choice === 'system') root.removeAttribute('data-theme');
  else root.setAttribute('data-theme', choice);
}

export function writeThemeChoice(choice: ThemeChoice): void {
  try {
    localStorage.setItem(STORAGE_KEY, choice);
  } catch {
    // Losing the preference costs a wrong theme next visit, never correctness.
  }
  applyTheme(choice);
}

/**
 * Apply the stored choice as early as possible.
 *
 * Called from `main.tsx` BEFORE React renders. Doing it in a component effect
 * instead would paint the OS theme first and then repaint — a white flash on
 * every load for anyone whose OS is light, which is exactly the population that
 * wants dark mode.
 */
export function initTheme(): void {
  applyTheme(readThemeChoice());
}
