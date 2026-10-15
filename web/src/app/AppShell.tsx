import { Link, Outlet, useRouterState } from '@tanstack/react-router';
import { useState } from 'react';
import { ThemeToggle } from '@/shared/ui/ThemeToggle';
import { GlobalSearch } from '@/features/search/GlobalSearch';
import { useLibrary } from '@/features/library/api';

/**
 * Application shell: sidebar, top bar, and the routed page.
 *
 * The shell is deliberately thin. It owns navigation chrome and nothing else -
 * no data fetching for pages, no page-specific state. That keeps every page
 * independently understandable and means adding a route touches one file.
 */

interface NavItem {
  to: string;
  label: string;
  icon: React.ReactNode;
}

const NAV: NavItem[] = [
  { to: '/photos', label: 'Pictures', icon: <IconPhotos /> },
  { to: '/albums', label: 'Albums', icon: <IconAlbums /> },
  { to: '/search', label: 'Search', icon: <IconSearch /> },
  { to: '/trash', label: 'Trash', icon: <IconTrash /> },
];

/** The library's machinery, separated from browsing by a divider. */
const OPS_NAV: NavItem[] = [
  { to: '/library', label: 'Library', icon: <IconLibrary /> },
  { to: '/settings', label: 'Settings', icon: <IconSettings /> },
];

export function AppShell() {
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const pathname = useRouterState({ select: (s) => s.location.pathname });

  return (
    <div className="flex h-full bg-ground text-ink">
      {/* Narrow-window scrim */}
      {sidebarOpen && (
        <button
          type="button"
          aria-label="Close navigation"
          className="fixed inset-0 z-30 bg-black/50 md:hidden"
          onClick={() => setSidebarOpen(false)}
        />
      )}

      <aside
        className={`fixed inset-y-0 left-0 z-40 flex w-60 shrink-0 flex-col border-r border-line bg-surface transition-transform duration-200 md:static md:translate-x-0 ${
          sidebarOpen ? 'translate-x-0' : '-translate-x-full'
        }`}
        style={{ transitionTimingFunction: 'var(--ease-out-soft)' }}
      >
        <div className="flex h-14 items-center gap-2.5 px-5">
          <span className="grid h-7 w-7 place-items-center rounded-md bg-accent text-sm font-bold text-ink-inverse">
            T
          </span>
          <span className="text-[15px] font-semibold tracking-tight">Tessera</span>
        </div>

        <nav className="flex-1 space-y-0.5 overflow-y-auto px-3 py-2">
          {NAV.map((item) => (
            <NavLink key={item.to} item={item} pathname={pathname} onNavigate={() => setSidebarOpen(false)} />
          ))}

          <div className="my-2 border-t border-line-soft" role="separator" />

          {OPS_NAV.map((item) => (
            <NavLink key={item.to} item={item} pathname={pathname} onNavigate={() => setSidebarOpen(false)} />
          ))}
        </nav>

        <LibraryFooter />
      </aside>

      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-14 shrink-0 items-center gap-3 border-b border-line bg-surface px-4">
          <button
            type="button"
            className="rounded-md p-2 text-ink-muted hover:bg-raised hover:text-ink md:hidden"
            onClick={() => setSidebarOpen(true)}
            aria-label="Open navigation"
          >
            <IconMenu />
          </button>

          <GlobalSearch />

          <div className="ml-auto flex items-center gap-2">
            {/* Theme sits in the header rather than a settings page: it is a
                preference someone changes by looking at the result, so it
                belongs where the result is visible. */}
            <ThemeToggle />
          </div>
        </header>

        <main className="min-h-0 flex-1">
          <Outlet />
        </main>
      </div>
    </div>
  );
}

/**
 * The numbers that matter at a glance, at the foot of the sidebar: how many
 * pictures, and whether the free tier has stopped indexing. The library
 * query is shared with the Library page, so this costs no extra request.
 */
function LibraryFooter() {
  const { data } = useLibrary();
  if (!data) return null;

  const pictures = data.roots.reduce((sum, root) => sum + root.counts.contents, 0);

  return (
    <div className="border-t border-line p-3 text-xs text-ink-faint">
      <p className="tabular-nums">
        <span className="text-ink-muted">{pictures.toLocaleString()}</span> pictures ·{' '}
        <span className="text-ink-muted">{data.roots.length}</span>{' '}
        {data.roots.length === 1 ? 'folder' : 'folders'}
      </p>
      {data.licence.capReached && (
        <Link
          to="/settings"
          hash="licence"
          className="mt-1.5 block rounded-md border border-accent/40 bg-accent-muted px-2 py-1.5 text-accent hover:border-accent"
        >
          Free tier: indexing stopped at {data.licence.cap.toLocaleString()}. Unlock →
        </Link>
      )}
    </div>
  );
}

function NavLink({
  item,
  pathname,
  onNavigate,
}: {
  item: NavItem;
  pathname: string;
  onNavigate: () => void;
}) {
  const active = pathname.startsWith(item.to);
  return (
    <Link
      to={item.to}
      onClick={onNavigate}
      className={`flex items-center gap-3 rounded-md px-3 py-2 text-sm transition-colors ${
        active
          ? 'bg-accent-muted font-medium text-accent'
          : 'text-ink-muted hover:bg-raised hover:text-ink'
      }`}
    >
      <span className="shrink-0">{item.icon}</span>
      {item.label}
    </Link>
  );
}

/* Inline SVG icons rather than an icon package: a handful of icons do not
   justify a dependency, and these inherit `currentColor` so they theme for
   free. */

function IconPhotos() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <rect x="3" y="3" width="18" height="18" rx="2" />
      <circle cx="8.5" cy="8.5" r="1.5" />
      <path d="m21 15-5-5L5 21" />
    </svg>
  );
}

function IconAlbums() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <path d="M3 7h6l2 2h10v10a2 2 0 0 1-2 2H3z" />
      <path d="M3 7V5a2 2 0 0 1 2-2h4" />
    </svg>
  );
}

function IconSearch() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <circle cx="11" cy="11" r="7" />
      <path d="m20 20-3.5-3.5" />
    </svg>
  );
}

function IconMenu() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <path d="M4 6h16M4 12h16M4 18h16" />
    </svg>
  );
}

function IconTrash() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <path d="M4 7h16M10 11v6M14 11v6" />
      <path d="M6 7l1 13a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1l1-13M9 7V4h6v3" />
    </svg>
  );
}

function IconLibrary() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <path d="M3 4h5l2 2h11v13H3z" />
      <path d="M3 10h18" />
    </svg>
  );
}

function IconSettings() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.7 1.7 0 0 0 .34 1.87l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.7 1.7 0 0 0-1.87-.34 1.7 1.7 0 0 0-1 1.56V21a2 2 0 1 1-4 0v-.09a1.7 1.7 0 0 0-1-1.56 1.7 1.7 0 0 0-1.87.34l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.7 1.7 0 0 0 .34-1.87 1.7 1.7 0 0 0-1.56-1H3a2 2 0 1 1 0-4h.09a1.7 1.7 0 0 0 1.56-1 1.7 1.7 0 0 0-.34-1.87l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.7 1.7 0 0 0 1.87.34h.01a1.7 1.7 0 0 0 1-1.56V3a2 2 0 1 1 4 0v.09a1.7 1.7 0 0 0 1 1.56 1.7 1.7 0 0 0 1.87-.34l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.7 1.7 0 0 0-.34 1.87v.01a1.7 1.7 0 0 0 1.56 1H21a2 2 0 1 1 0 4h-.09a1.7 1.7 0 0 0-1.56 1z" />
    </svg>
  );
}
