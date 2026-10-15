/**
 * Search filter state — parsing, serialising, and turning it into Tessera's
 * query parameters.
 *
 * Every filter lives in the URL, not in component state. That single decision
 * buys three behaviours for free: a filtered search is shareable as a link,
 * survives a refresh, and works with the back button. The cost is that the URL
 * is now an UNTRUSTED input — anyone can hand-edit it — so everything here
 * parses defensively and drops what it does not recognise.
 *
 * The route's `validateSearch` (in router.tsx, which is shared and not ours to
 * edit) only declares `q` and `albumId`. The router passes unknown params
 * through to the URL untouched — its strict-search stripping is opt-in and not
 * enabled — so the extra params below ride alongside and are read back with
 * `useSearch({ strict: false })`.
 *
 * This module is pure on purpose: no hooks, no fetching. Pure functions are the
 * part of a search page that can actually be unit-tested, so the parsing and
 * DTO-building logic lives here and the components stay thin.
 */

export type SearchMediaType = 'IMAGE' | 'VIDEO';

export interface SearchFilters {
  /** Absent means "all media". */
  type?: SearchMediaType;
  /** Calendar dates as the `<input type="date">` gives them: YYYY-MM-DD. */
  takenAfter?: string;
  takenBefore?: string;
  /**
   * When true, the query text matches file NAMES and the filters alone are a
   * search ("all videos from 2016"), with no embedding involved. Otherwise
   * Foundation ranks the library by what is in the picture.
   */
  metadataOnly: boolean;
}

export const EMPTY_FILTERS: SearchFilters = { metadataOnly: false };

/** YYYY-MM-DD, as produced by a date input. Anything else is dropped. */
const DATE_RE = /^\d{4}-\d{2}-\d{2}$/;

function cleanString(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined;
  const trimmed = value.trim();
  return trimmed ? trimmed : undefined;
}

function cleanDate(value: unknown): string | undefined {
  const str = cleanString(value);
  return str && DATE_RE.test(str) ? str : undefined;
}

/**
 * Read filters out of the raw URL search object.
 *
 * Invalid values are silently dropped rather than surfaced as errors: a
 * mangled URL should degrade to "fewer filters applied", not a broken page.
 */
export function parseFilters(raw: Record<string, unknown>): SearchFilters {
  const type = raw['type'];

  return {
    ...(type === 'IMAGE' || type === 'VIDEO' ? { type } : {}),
    ...spread('takenAfter', cleanDate(raw['after'])),
    ...spread('takenBefore', cleanDate(raw['before'])),
    metadataOnly: raw['mode'] === 'metadata',
  };
}

/**
 * Serialise filters back into URL params. The exact inverse of `parseFilters`.
 *
 * Inactive filters serialise to `undefined`, which TanStack Router omits from
 * the URL entirely — so an unfiltered search is still just `?q=sunset`.
 */
export function filtersToUrl(filters: SearchFilters): Record<string, string | undefined> {
  return {
    type: filters.type,
    after: filters.takenAfter,
    before: filters.takenBefore,
    mode: filters.metadataOnly ? 'metadata' : undefined,
  };
}

/**
 * How many filters narrow the result set. The mode toggle is excluded: it
 * changes HOW we search, not WHAT matches, so it neither lights up the
 * filter-count badge nor counts as searchable input on its own.
 */
export function countActiveFilters(filters: SearchFilters): number {
  return [filters.type, filters.takenAfter, filters.takenBefore].filter((value) => value !== undefined).length;
}

/**
 * Whether there is enough input to run a search at all.
 *
 * Semantic search embeds the query text, so it needs text — two characters or
 * more, matching what the search box enforces. Metadata search is a plain
 * database query, so filters alone are a perfectly good search: "all videos
 * from 2016" needs no text at all.
 */
export function hasSearchableInput(q: string, albumId: string | undefined, filters: SearchFilters): boolean {
  if (!filters.metadataOnly) return q.trim().length > 1;
  return q.trim().length > 0 || albumId !== undefined || countActiveFilters(filters) > 0;
}

/**
 * Convert the calendar dates to the ISO instants the API wants.
 *
 * `after` becomes midnight UTC at the START of that day, `before` the very END
 * of its day — so "before 2020-05-01" includes photos taken ON May 1st, which
 * is what anyone picking a date in a filter means. Boundaries are UTC rather
 * than local time: the server stores UTC, and a timezone-exact day boundary is
 * not worth the confusion for a filter whose granularity is a whole day.
 */
export function toApiDateRange(filters: SearchFilters): { takenAfter?: string; takenBefore?: string } {
  return {
    ...spread('takenAfter', filters.takenAfter ? `${filters.takenAfter}T00:00:00.000Z` : undefined),
    ...spread('takenBefore', filters.takenBefore ? `${filters.takenBefore}T23:59:59.999Z` : undefined),
  };
}

/**
 * Tessera's query parameters for one page of a search. The same four filters
 * apply in both modes; `mode=metadata` switches the server from ranking by
 * the words to matching the file name.
 */
export function buildSearchParams(
  q: string,
  albumId: string | undefined,
  filters: SearchFilters,
  page: number,
  size: number,
): URLSearchParams {
  const params = new URLSearchParams({ page: String(page), size: String(size) });
  const query = q.trim();
  if (query) params.set('q', query);
  if (albumId) params.set('albumId', albumId);
  if (filters.metadataOnly) params.set('mode', 'metadata');
  if (filters.type) params.set('type', filters.type);
  const range = toApiDateRange(filters);
  if (range.takenAfter) params.set('takenAfter', range.takenAfter);
  if (range.takenBefore) params.set('takenBefore', range.takenBefore);
  return params;
}

/**
 * Spread helper for `exactOptionalPropertyTypes`.
 *
 * Under that flag, `{ make: undefined }` is NOT assignable to `{ make?: string }`
 * — the key must be absent, not present-and-undefined. Building objects with
 * conditional spreads satisfies it; this helper keeps those spreads readable.
 */
function spread<K extends string, V>(key: K, value: V | undefined): { [P in K]?: V } {
  return value === undefined ? {} : ({ [key]: value } as { [P in K]?: V });
}
