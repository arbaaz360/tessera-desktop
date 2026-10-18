/**
 * Whether the welcome page has been passed by.
 *
 * A library with no folders used to be sent to the welcome page every time,
 * which made "choose a folder" the only way in. Pictures can now be dropped
 * or picked straight onto the grid, so the welcome page is shown on a first
 * run and skippable; once skipped (or once something was added from it) the
 * empty grid is where the app opens. Per machine, in localStorage: a lost
 * flag only shows the welcome page once more.
 */
const KEY = 'tessera.welcome.skipped';

export function welcomeSkipped(): boolean {
  try {
    return localStorage.getItem(KEY) === '1';
  } catch {
    return false;
  }
}

export function skipWelcome(): void {
  try {
    localStorage.setItem(KEY, '1');
  } catch {
    // Private mode or storage blocked: the welcome page shows again next time.
  }
}
