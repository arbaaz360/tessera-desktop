/**
 * Reload when a newer build has been deployed.
 *
 * The app is a long-lived tab: it stays open for days, and a deploy replaces
 * the bundle under it without the tab noticing - every fix then looks "not
 * fixed" until someone thinks to reload. index.html is served no-cache and
 * names the current bundle, so the check is one small GET: whenever the tab
 * comes back into view, compare the bundle index.html names with the one
 * running here, and reload if they differ. Never more than once a minute,
 * and never while the user is mid-gesture - focus/visibility is the moment
 * they are coming back, not the moment they are doing something.
 */
const MIN_GAP_MS = 60_000;
let lastCheck = 0;

function runningBundle(): string | null {
  for (const script of Array.from(document.scripts)) {
    const match = /\/assets\/(index-[^/]+\.js)$/.exec(script.src);
    if (match) return match[1] ?? null;
  }
  return null;
}

async function deployedBundle(): Promise<string | null> {
  const response = await fetch('/index.html', { cache: 'no-store' });
  if (!response.ok) return null;
  const match = /assets\/(index-[^"']+\.js)/.exec(await response.text());
  return match?.[1] ?? null;
}

export async function reloadIfStale(): Promise<void> {
  const now = Date.now();
  if (now - lastCheck < MIN_GAP_MS) return;
  lastCheck = now;

  const running = runningBundle();
  if (!running) return;
  try {
    const deployed = await deployedBundle();
    if (deployed && deployed !== running) {
      window.location.reload();
    }
  } catch {
    /* offline or the server is restarting: try again next time */
  }
}

/** Wire the check to the moments a person comes back to the tab. */
export function watchForNewBuilds(): () => void {
  const onVisible = () => {
    if (document.visibilityState === 'visible') void reloadIfStale();
  };
  document.addEventListener('visibilitychange', onVisible);
  window.addEventListener('focus', onVisible);
  return () => {
    document.removeEventListener('visibilitychange', onVisible);
    window.removeEventListener('focus', onVisible);
  };
}
