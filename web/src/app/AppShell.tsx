import { Link, Outlet, useRouterState } from '@tanstack/react-router';
import { useState } from 'react';
import { useMe } from '@/shared/api/queries';
import { ThemeToggle } from '@/shared/ui/ThemeToggle';
import { GlobalSearch } from '@/features/search/GlobalSearch';
import { AccountMenu } from '@/features/auth/AccountMenu';

/**
 * Application shell: sidebar, top bar, and the routed page.
 *
 * The shell is deliberately thin. It owns navigation chrome and nothing else —
 * no data fetching for pages, no page-specific state. That keeps every page
 * independently understandable and means adding a route touches one file.
 */

interface NavItem {
  to: string;
  label: string;
  icon: React.ReactNode;
}

const NAV: NavItem[] = [
  { to: '/photos', label: 'Photos', icon: <IconPhotos /> },
  { to: '/albums', label: 'Albums', icon: <IconAlbums /> },
  { to: '/faces', label: 'Find a face', icon: <IconFaceSearch /> },
  { to: '/search', label: 'Search', icon: <IconSearch /> },
  { to: '/music', label: 'Music', icon: <IconMusic /> },
  { to: '/trash', label: 'Trash', icon: <IconTrash /> },
];

/**
 * Operational pages, separated from browsing by a divider. Jobs is visible to
 * everyone — watching a queue drain harms nothing — while Admin appears only
 * for admin accounts, matching what the server will authorize anyway.
 */
const OPS_NAV: NavItem[] = [
  { to: '/jobs', label: 'Jobs', icon: <IconJobs /> },
  { to: '/admin', label: 'Admin', icon: <IconAdmin /> },
];

export function AppShell() {
  const { data: me } = useMe();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const pathname = useRouterState({ select: (s) => s.location.pathname });

  return (
    <div className="flex h-full bg-ground text-ink">
      {/* Mobile scrim */}
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
            P
          </span>
          <span className="text-[15px] font-semibold tracking-tight">Photos</span>
        </div>

        <nav className="flex-1 space-y-0.5 overflow-y-auto px-3 py-2">
          {NAV.map((item) => (
            <NavLink key={item.to} item={item} pathname={pathname} onNavigate={() => setSidebarOpen(false)} />
          ))}

          <div className="my-2 border-t border-line-soft" role="separator" />

          {OPS_NAV.filter((item) => item.to !== '/admin' || me?.isAdmin).map((item) => (
            <NavLink key={item.to} item={item} pathname={pathname} onNavigate={() => setSidebarOpen(false)} />
          ))}

        </nav>

        {me && <AccountMenu me={me} />}
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
                per-device preference someone changes by looking at the result,
                so it belongs where the result is visible. */}
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

function IconFaceSearch() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" aria-hidden>
      <circle cx="10" cy="8" r="4" />
      <path d="M3 20c0-3.5 3-6 7-6 1.2 0 2.3.2 3.2.6" />
      <circle cx="17.5" cy="16.5" r="3" />
      <path d="m19.8 18.8 2 2" />
    </svg>
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

function IconMusic() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <path d="M9 18V6l11-2v12" />
      <circle cx="6.5" cy="18" r="2.5" />
      <circle cx="17.5" cy="16" r="2.5" />
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

function IconJobs() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <path d="M4 17l5-5-5-5M12 19h8" />
    </svg>
  );
}

function IconAdmin() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.7 1.7 0 0 0 .34 1.87l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.7 1.7 0 0 0-1.87-.34 1.7 1.7 0 0 0-1 1.56V21a2 2 0 1 1-4 0v-.09a1.7 1.7 0 0 0-1-1.56 1.7 1.7 0 0 0-1.87.34l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.7 1.7 0 0 0 .34-1.87 1.7 1.7 0 0 0-1.56-1H3a2 2 0 1 1 0-4h.09a1.7 1.7 0 0 0 1.56-1 1.7 1.7 0 0 0-.34-1.87l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.7 1.7 0 0 0 1.87.34h.01a1.7 1.7 0 0 0 1-1.56V3a2 2 0 1 1 4 0v.09a1.7 1.7 0 0 0 1 1.56 1.7 1.7 0 0 0 1.87-.34l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.7 1.7 0 0 0-.34 1.87v.01a1.7 1.7 0 0 0 1.56 1H21a2 2 0 1 1 0 4h-.09a1.7 1.7 0 0 0-1.56 1z" />
    </svg>
  );
}
