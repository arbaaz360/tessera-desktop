import { useCallback, useEffect, useState } from 'react';
import type { AlbumSort, AlbumsView } from './organize';

/**
 * The albums page's view preferences, persisted in localStorage.
 *
 * Sort, view and grouping are DURABLE preferences — "I like my albums as a
 * dated list" is true tomorrow too, so the page should reopen the way it was
 * left. Contrast the collapsed-year sections, which stay in component state:
 * collapsing 2019 is something you do while scanning today, not a lasting
 * opinion. Persisting one and not the other is a deliberate line, not an
 * oversight.
 *
 * localStorage rather than the URL because these are preferences, not
 * location: a shared /albums link should open MY albums page my way, not
 * impose the sender's sort order on me.
 */

export interface AlbumsViewPrefs {
  sort: AlbumSort;
  view: AlbumsView;
  groupByYear: boolean;
  /** Edge, in px, of the thumbnails in the hover cover-suggestion flyout. */
  suggestionSize: number;
}

const STORAGE_KEY = 'photos-web.albums.view';

/**
 * Bounds for the suggestion-thumbnail slider. Below 72px a face is a smudge
 * (the failure the slider exists to fix); above 220px five thumbnails plus
 * the panel chrome outgrow a 1280px viewport.
 */
export const SUGGESTION_SIZE_MIN = 72;
export const SUGGESTION_SIZE_MAX = 220;

export const DEFAULT_PREFS: AlbumsViewPrefs = {
  // Newest first: recency is how people think about a photo library. The
  // page's first iteration sorted by size, which is a diagnostic order — fine
  // for auditing an import, odd for browsing.
  sort: 'newest',
  view: 'grid',
  groupByYear: false,
  suggestionSize: 120,
};

const SORTS: readonly AlbumSort[] = ['name', 'newest', 'oldest', 'most'];
const VIEWS: readonly AlbumsView[] = ['grid', 'list'];

function clampSuggestionSize(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) return DEFAULT_PREFS.suggestionSize;
  return Math.round(Math.min(SUGGESTION_SIZE_MAX, Math.max(SUGGESTION_SIZE_MIN, value)));
}

/**
 * Parse a stored value back into preferences, defensively.
 *
 * localStorage is user-writable and survives deploys, so whatever is in there
 * is INPUT, not state we control — an old schema, a typo from devtools, or
 * `"undefined"`. Each field is validated against the known values and falls
 * back independently; one bad field should not discard the other two.
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
    suggestionSize: clampSuggestionSize(candidate.suggestionSize),
  };
}

/**
 * The hook: preferences plus a patch-style updater.
 *
 * Reading happens once, in the lazy `useState` initialiser — not in an effect,
 * which would flash the defaults for a frame before the stored value arrived.
 * Writing happens in an effect keyed on the value, so persistence is a
 * consequence of the state changing rather than something every call site must
 * remember to do. Both sides are wrapped in try/catch: storage can be full or
 * blocked (private windows), and losing persistence must never take the page
 * down with it.
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
