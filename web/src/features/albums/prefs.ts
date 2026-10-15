import { useCallback, useEffect, useState } from 'react';
import type { AlbumSort, AlbumsView } from './organize';

/**
 * The albums page's view preferences, persisted in localStorage.
 *
 * Sort, view and grouping are DURABLE preferences - "I like my albums as a
 * dated list" is true tomorrow too, so the page should reopen the way it was
 * left. Contrast the collapsed-year sections, which stay in component state:
 * collapsing 2019 is something you do while scanning today, not a lasting
 * opinion.
 */

export interface AlbumsViewPrefs {
  sort: AlbumSort;
  view: AlbumsView;
  groupByYear: boolean;
}

const STORAGE_KEY = 'tessera.albums.view';

export const DEFAULT_PREFS: AlbumsViewPrefs = {
  // Newest first: recency is how people think about a picture library.
  sort: 'newest',
  view: 'grid',
  groupByYear: false,
};

const SORTS: readonly AlbumSort[] = ['name', 'newest', 'oldest', 'most'];
const VIEWS: readonly AlbumsView[] = ['grid', 'list'];

/**
 * Parse a stored value back into preferences, defensively.
 *
 * localStorage is user-writable and survives updates, so whatever is in there
 * is INPUT, not state we control. Each field is validated against the known
 * values and falls back independently; one bad field should not discard the
 * other two.
 */
export function parseViewPrefs(raw: string | null): AlbumsViewPrefs {
  if (!raw) return DEFAULT_PREFS;

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return DEFAULT_PREFS;
  }
  if (typeof parsed !== 'object' || parsed === null) return DEFAULT_PREFS;

  const candidate = parsed as Record<string, unknown>;
  return {
    sort: SORTS.includes(candidate.sort as AlbumSort) ? (candidate.sort as AlbumSort) : DEFAULT_PREFS.sort,
    view: VIEWS.includes(candidate.view as AlbumsView) ? (candidate.view as AlbumsView) : DEFAULT_PREFS.view,
    groupByYear:
      typeof candidate.groupByYear === 'boolean' ? candidate.groupByYear : DEFAULT_PREFS.groupByYear,
  };
}

/**
 * The hook: preferences plus a patch-style updater. Reading happens once, in
 * the lazy `useState` initialiser, so the defaults never flash; writing is a
 * consequence of the state changing. Both sides tolerate blocked storage.
 */
export function useAlbumsViewPrefs(): [AlbumsViewPrefs, (patch: Partial<AlbumsViewPrefs>) => void] {
  const [prefs, setPrefs] = useState<AlbumsViewPrefs>(() => {
    try {
      return parseViewPrefs(localStorage.getItem(STORAGE_KEY));
    } catch {
      return DEFAULT_PREFS;
    }
  });

  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(prefs));
    } catch {
      // Storage being unavailable degrades to session-only preferences.
    }
  }, [prefs]);

  const update = useCallback((patch: Partial<AlbumsViewPrefs>) => {
    setPrefs((current) => ({ ...current, ...patch }));
  }, []);

  return [prefs, update];
}
