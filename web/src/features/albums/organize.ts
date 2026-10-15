/**
 * Sorting, grouping and flattening for the albums page — pure functions only.
 *
 * Everything here is deliberately free of React and free of fetching: given the
 * same albums and the same options, you get the same output. That is what makes
 * this file testable with plain vitest and the page component thin — the
 * component's job shrinks to "hold the options, render the result".
 *
 * The functions accept a structural subset of `AlbumResponseDto` rather than
 * the full DTO. Tests can then construct five-field fixtures instead of
 * seventeen-field ones, and the signatures document exactly which fields the
 * logic actually reads.
 */

export type AlbumSort = 'name' | 'newest' | 'oldest' | 'most';
export type AlbumsView = 'grid' | 'list';

/** The fields this module reads. `AlbumResponseDto` satisfies it structurally. */
export interface AlbumLike {
  id: string;
  albumName: string;
  assetCount: number;
  /** Creation date — always present on the DTO. */
  createdAt: string;
  /** Date of the latest asset — absent on empty albums. */
  endDate?: string;
}

/**
 * An album's effective date, in epoch milliseconds.
 *
 * `endDate` (the latest asset) is preferred because it reflects the photos;
 * `createdAt` only says when the album record was made, which for a Takeout
 * import is "the day of the migration" for every album at once. It is still a
 * better fallback than nothing for empty albums. `null` means neither field
 * parsed — the album is genuinely undated.
 */
export function albumDateMs(album: AlbumLike): number | null {
  for (const value of [album.endDate, album.createdAt]) {
    if (!value) continue;
    const ms = Date.parse(value);
    if (!Number.isNaN(ms)) return ms;
  }
  return null;
}

export const UNDATED_LABEL = 'Undated';

/** The year an album groups under, e.g. "2021" — or "Undated". */
export function albumYearLabel(album: AlbumLike): string {
  const ms = albumDateMs(album);
  return ms === null ? UNDATED_LABEL : String(new Date(ms).getFullYear());
}

/**
 * Name comparison via `localeCompare` with `numeric: true`, so "Trip 2" sorts
 * before "Trip 10" — plain string comparison puts them the other way around,
 * which reads as a bug to anyone who names albums with numbers.
 */
function byName(a: AlbumLike, b: AlbumLike): number {
  return a.albumName.localeCompare(b.albumName, undefined, { numeric: true, sensitivity: 'base' });
}

/**
 * Sort a copy of the list. Every mode falls back to the name comparison for
 * ties, so the order is total and stable across refetches — without a
 * deterministic tie-break, equal-count albums would jiggle every time the
 * server returned them in a different order.
 *
 * Undated albums sink to the end of both date sorts: "I don't know when this
 * was" belongs after every album we do know about, in either direction.
 */
export function sortAlbums<A extends AlbumLike>(albums: readonly A[], sort: AlbumSort): A[] {
  const copy = [...albums];

  switch (sort) {
    case 'name':
      return copy.sort(byName);
    case 'most':
      return copy.sort((a, b) => b.assetCount - a.assetCount || byName(a, b));
    case 'newest':
    case 'oldest': {
      const direction = sort === 'newest' ? -1 : 1;
      return copy.sort((a, b) => {
        const dateA = albumDateMs(a);
        const dateB = albumDateMs(b);
        if (dateA === null && dateB === null) return byName(a, b);
        if (dateA === null) return 1;
        if (dateB === null) return -1;
        return (dateA - dateB) * direction || byName(a, b);
      });
    }
  }
}

export interface AlbumYearGroup<A extends AlbumLike> {
  label: string;
  albums: A[];
}

/**
 * Group an already-sorted list by year.
 *
 * Group ORDER is a separate decision from album order: with "oldest first" the
 * years must ascend, with everything else they descend (name and count sorts
 * have no inherent year direction, and newest-first is the natural default).
 * The caller passes the direction; albums keep their incoming order inside
 * each group. Undated is always last — in either direction it is the group of
 * least interest.
 */
export function groupAlbumsByYear<A extends AlbumLike>(
  albums: readonly A[],
  yearOrder: 'asc' | 'desc',
): AlbumYearGroup<A>[] {
  const byLabel = new Map<string, A[]>();
  for (const album of albums) {
    const label = albumYearLabel(album);
    const bucket = byLabel.get(label);
    if (bucket) bucket.push(album);
    else byLabel.set(label, [album]);
  }

  const labels = [...byLabel.keys()].sort((a, b) => {
    if (a === UNDATED_LABEL) return 1;
    if (b === UNDATED_LABEL) return -1;
    return yearOrder === 'asc' ? Number(a) - Number(b) : Number(b) - Number(a);
  });

  return labels.map((label) => ({ label, albums: byLabel.get(label) ?? [] }));
}

/**
 * The flattened render list.
 *
 * The page virtualises ONE list regardless of view or grouping, so every
 * combination — grid/list × grouped/flat × collapsed/open — must reduce to a
 * flat sequence of fixed-height items. Doing that reduction here, as data,
 * means the component never branches on "grouped grid" vs "flat list": it maps
 * items to elements and the virtualiser does the rest.
 *
 * `key` is precomputed for the virtualiser: keys must be stable while
 * filtering and collapsing shuffle indexes around, or React remounts every row
 * on each keystroke.
 */
export type AlbumsRenderItem<A extends AlbumLike> =
  | { kind: 'header'; key: string; label: string; count: number; collapsed: boolean }
  | { kind: 'grid-row'; key: string; albums: A[] }
  | { kind: 'list-row'; key: string; album: A };

export interface RenderListOptions {
  view: AlbumsView;
  /** Columns per grid row. Ignored for the list view. */
  columns: number;
  /** null = no grouping; otherwise the year direction (see groupAlbumsByYear). */
  groupOrder: 'asc' | 'desc' | null;
  /** Labels of collapsed year sections. */
  collapsed: ReadonlySet<string>;
}

export function buildRenderList<A extends AlbumLike>(
  albums: readonly A[],
  options: RenderListOptions,
): AlbumsRenderItem<A>[] {
  const columns = Math.max(1, options.columns);
  const items: AlbumsRenderItem<A>[] = [];

  const pushAlbums = (group: readonly A[]) => {
    if (options.view === 'list') {
      for (const album of group) items.push({ kind: 'list-row', key: album.id, album });
      return;
    }
    for (let start = 0; start < group.length; start += columns) {
      const slice = group.slice(start, start + columns);
      // Keyed by the first album in the row: stable as long as the row's
      // leading album is, which survives appends and most filters.
      items.push({ kind: 'grid-row', key: `row:${slice[0]?.id ?? start}`, albums: slice });
    }
  };

  if (options.groupOrder === null) {
    pushAlbums(albums);
    return items;
  }

  for (const group of groupAlbumsByYear(albums, options.groupOrder)) {
    const collapsed = options.collapsed.has(group.label);
    items.push({
      kind: 'header',
      key: `year:${group.label}`,
      label: group.label,
      count: group.albums.length,
      collapsed,
    });
    // A collapsed section keeps its header (with the count, so nothing looks
    // lost) and simply contributes no rows.
    if (!collapsed) pushAlbums(group.albums);
  }

  return items;
}
