import { useDeferredValue, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { Link, useNavigate } from '@tanstack/react-router';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useAlbums } from '@/shared/api/queries';
import { assetThumbnailUrl } from '@/shared/api/client';
import type { AlbumResponseDto } from '@immich/sdk';
import { CoverPickerModal } from '@/features/covers/CoverPickerModal';
import { HoverCandidates } from '@/features/covers/HoverCandidates';
import { MergeBar } from './MergeBar';
import { OverlapBadge } from './OverlapBadge';
import { useAlbumOverlap, type OverlapEntry } from './overlap';
import { useCoverPickerHealth } from '@/features/covers/api';
import { albumDateMs, buildRenderList, sortAlbums, type AlbumSort, type AlbumsView } from './organize';
import { SUGGESTION_SIZE_MAX, SUGGESTION_SIZE_MIN, useAlbumsViewPrefs } from './prefs';
import { useCreateAlbum } from './api';

/**
 * Album browser.
 *
 * This page replaces two patches the previous deployment carried, and it is
 * worth understanding why neither is needed here.
 *
 * THE 42-SECOND PROBLEM. The old UI filtered albums by media count, and got
 * those counts by calling a helper service that ran a GROUP BY across 1.6M
 * album memberships on every page load. Un-cached, it took 42 seconds, and an
 * injected fetch wrapper blocked the album list on it. But `AlbumResponseDto`
 * already includes `assetCount` — the data was in the response the whole time.
 * Filtering here is free.
 *
 * THE PAGER. The old UI hand-edited a compiled chunk to slice 4,878 albums into
 * pages, because rendering them all froze the tab. Virtualisation solves the
 * same problem better: one continuous scroll, only visible rows mounted, no
 * pagination state to keep in sync with filtering.
 *
 * The page itself stays thin by design. Sorting, year grouping and the
 * flattened render list are pure functions in `organize.ts`; the durable view
 * preferences live in `prefs.ts`; the mutations in `api.ts`. What remains here
 * is the part only a component can do: hold the ephemeral state and map data
 * to elements.
 */

/**
 * Which card currently owns the cover-suggestion flyout, if any.
 *
 * Module scope rather than React state, deliberately. The flyout is pinned to
 * the top of the viewport, so reaching it from a card lower down means dragging
 * the pointer across other cards — each of which would otherwise arm its own
 * flyout mid-journey and steal the panel out from under the cursor.
 *
 * One shared variable makes the open flyout exclusive: while one is up, no
 * other card arms. Lifting this into the page's state would re-render all 4,865
 * cards on every hover, which is precisely the cost this avoids — and nothing
 * here needs to trigger a render, only to suppress one.
 */
let flyoutOwner: string | null = null;

const MIN_COUNT_PRESETS = [0, 10, 30, 100] as const;
const CARD_MIN_WIDTH = 190;
const ROW_GAP = 20;
/** Fixed heights for the two non-grid item kinds, so the virtualiser can
    position everything without measuring the DOM. */
const HEADER_HEIGHT = 44;
const LIST_ROW_HEIGHT = 48;

const SORT_OPTIONS: { value: AlbumSort; label: string }[] = [
  { value: 'newest', label: 'Newest first' },
  { value: 'oldest', label: 'Oldest first' },
  { value: 'name', label: 'Name' },
  { value: 'most', label: 'Most items' },
];

export function AlbumsPage() {
  const { data: albums, isPending, isError, isFetching } = useAlbums();
  const [prefs, updatePrefs] = useAlbumsViewPrefs();

  const [term, setTerm] = useState('');
  const [minCount, setMinCount] = useState<number>(0);
  const [creating, setCreating] = useState(false);
  const [coverTarget, setCoverTarget] = useState<AlbumResponseDto | null>(null);

  // Which of these albums also exist in the other account. An empty map means
  // either "not loaded yet" or "this account is not part of the split library"
  // — both render identically (no badges, no filter button), which is the
  // correct behaviour for both.
  const overlap = useAlbumOverlap();
  const [bothOnly, setBothOnly] = useState(false);

  // Merge selection, held as IDS rather than album objects so it survives the
  // list being refiltered, resorted or refetched underneath it. Albums worth
  // merging are usually far apart in 4,865 cards, so the intended flow is:
  // filter by name, tick one, clear the filter, filter again, tick another.
  const [selectedIds, setSelectedIds] = useState<ReadonlySet<string>>(new Set());

  const toggleSelected = (albumId: string) =>
    setSelectedIds((current) => {
      const next = new Set(current);
      if (next.has(albumId)) next.delete(albumId);
      else next.add(albumId);
      return next;
    });

  // One health probe gates every "pick cover" affordance on the page. When the
  // sidecar is down the affordances simply do not render — the feature
  // disappears quietly instead of offering a button that can only fail.
  const coverHealth = useCoverPickerHealth();
  const canPickCover = coverHealth.data === true;

  // Typing filters 4,878 items on every keystroke. `useDeferredValue` lets
  // React keep the input responsive and render the filtered grid at lower
  // priority — the cheapest possible fix for this exact shape of problem.
  const deferredTerm = useDeferredValue(term);

  const filtered = useMemo(() => {
    if (!albums) return [];
    const needle = deferredTerm.trim().toLowerCase();

    return albums
      .filter((album) => album.assetCount >= minCount)
      .filter((album) => (bothOnly ? overlap.has(album.id) : true))
      .filter((album) => (needle ? album.albumName.toLowerCase().includes(needle) : true));
  }, [albums, deferredTerm, minCount, bothOnly, overlap]);

  const sorted = useMemo(() => sortAlbums(filtered, prefs.sort), [filtered, prefs.sort]);

  // Year direction follows the sort: "oldest first" would look broken under
  // descending year headers. Every other sort defaults to newest-year-first
  // (see groupAlbumsByYear for the reasoning).
  const groupOrder = prefs.groupByYear ? (prefs.sort === 'oldest' ? 'asc' : 'desc') : null;

  if (isPending) return <AlbumsSkeleton />;
  if (isError) {
    return <p className="p-8 text-center text-ink-muted">Could not load albums.</p>;
  }

  return (
    <div className="flex h-full flex-col">
      <div className="flex flex-wrap items-center gap-3 border-b border-line-soft px-4 py-2.5">
        <input
          type="search"
          value={term}
          onChange={(event) => setTerm(event.target.value)}
          placeholder="Filter albums"
          aria-label="Filter albums by name"
          className="w-56 rounded-md border border-line bg-surface px-3 py-1.5 text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none"
        />

        <fieldset className="inline-flex items-center rounded-lg border border-line bg-surface p-0.5" aria-label="Minimum items">
          {MIN_COUNT_PRESETS.map((preset) => (
            <label
              key={preset}
              className={`cursor-pointer rounded-md px-2.5 py-1.5 text-[13px] font-medium transition-colors ${
                preset === minCount ? 'bg-accent text-ink-inverse' : 'text-ink-muted hover:text-ink'
              }`}
            >
              <input
                type="radio"
                name="min-count"
                checked={preset === minCount}
                onChange={() => setMinCount(preset)}
                className="sr-only"
              />
              {preset === 0 ? 'Any size' : `${preset}+`}
            </label>
          ))}
        </fieldset>

        {/* A native select: four options do not justify a custom dropdown, and
            the native one brings keyboard and screen-reader behaviour for
            free. Its value writes straight to the persisted preferences. */}
        <label className="flex items-center gap-1.5 text-sm text-ink-faint">
          Sort
          <select
            value={prefs.sort}
            onChange={(event) => updatePrefs({ sort: event.target.value as AlbumSort })}
            className="rounded-md border border-line bg-surface px-2 py-1.5 text-sm text-ink focus:border-accent focus:outline-none"
          >
            {SORT_OPTIONS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </label>

        <button
          type="button"
          aria-pressed={prefs.groupByYear}
          onClick={() => updatePrefs({ groupByYear: !prefs.groupByYear })}
          className={`rounded-md border px-2.5 py-1.5 text-[13px] font-medium transition-colors ${
            prefs.groupByYear
              ? 'border-accent bg-accent-muted text-ink'
              : 'border-line text-ink-muted hover:text-ink'
          }`}
        >
          Group by year
        </button>

        {/* Only offered where it means something: signed in as an account
            outside the split library the map is empty, and a filter that can
            only ever return nothing is worse than no filter. Component state,
            not a persisted preference — "show me the overlapping ones" is a
            task you are doing now, not how you want albums to look forever. */}
        {overlap.size > 0 && (
          <button
            type="button"
            aria-pressed={bothOnly}
            onClick={() => setBothOnly((current) => !current)}
            title="Show only albums that also exist in the other account"
            className={`rounded-md border px-2.5 py-1.5 text-[13px] font-medium transition-colors ${
              bothOnly
                ? 'border-accent bg-accent-muted text-ink'
                : 'border-line text-ink-muted hover:text-ink'
            }`}
          >
            In both accounts
          </button>
        )}

        <ViewToggle view={prefs.view} onChange={(view) => updatePrefs({ view })} />

        {/* When the ranking worker is down the suggestion features vanish,
            which is right for an optional service but a terrible way to
            communicate something FIXABLE - the hover strip simply stops
            existing, with nothing on screen to say so. A quiet line beats a
            silent absence. Tessera reports the worker at /api/workers. */}
        {coverHealth.isSuccess && !canPickCover && (
          <span
            className="text-xs text-ink-faint"
            title="Tessera cannot reach the cover-picker worker. Cover suggestions are unavailable until it responds."
          >
            suggestions offline
          </span>
        )}

        {/* Size of the hover cover-suggestion thumbnails. A live slider, not a
            setting page: the right size is found by looking, so the control
            sits where the effect is visible. Persisted with the other view
            preferences. */}
        <label
          className="flex items-center gap-1.5 text-sm text-ink-faint"
          title="Size of the cover suggestions shown when hovering an album"
        >
          Suggestions
          <input
            type="range"
            min={SUGGESTION_SIZE_MIN}
            max={SUGGESTION_SIZE_MAX}
            step={4}
            value={prefs.suggestionSize}
            onChange={(event) => updatePrefs({ suggestionSize: Number(event.target.value) })}
            aria-label="Cover suggestion thumbnail size"
            className="w-24 accent-[var(--color-accent,#e0a458)]"
          />
        </label>

        <div className="ml-auto flex items-center gap-3">
          <span className="text-sm tabular-nums text-ink-faint">
            {filtered.length.toLocaleString()} of {albums.length.toLocaleString()}
          </span>

          {/* The list you are looking at came from the last visit; the server
              takes ~7s to recount 2,422 albums. Saying so is the honest
              counterpart to painting instantly — without it, an album renamed
              on another device would look like a bug rather than a pending
              refresh. */}
          {isFetching && (
            <span className="text-xs text-ink-faint" role="status">
              refreshing…
            </span>
          )}

          <button
            type="button"
            onClick={() => setCreating(true)}
            className="inline-flex items-center gap-1.5 rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-ink-inverse transition-colors hover:bg-accent-hover"
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" aria-hidden>
              <path d="M12 5v14M5 12h14" />
            </svg>
            New album
          </button>
        </div>
      </div>

      <AlbumsScroller
        albums={sorted}
        view={prefs.view}
        groupOrder={groupOrder}
        canPickCover={canPickCover}
        suggestionSize={prefs.suggestionSize}
        selectedIds={selectedIds}
        overlap={overlap}
        onToggleSelected={toggleSelected}
        onPickCover={setCoverTarget}
      />

      {/* Resolved from the full album list, not the filtered one — a selected
          album must not vanish from the bar because the filter moved on. */}
      <MergeBar
        selected={(albums ?? []).filter((album) => selectedIds.has(album.id))}
        onClear={() => setSelectedIds(new Set())}
        onRemove={toggleSelected}
      />

      {creating && <NewAlbumDialog onClose={() => setCreating(false)} />}

      {coverTarget && (
        <CoverPickerModal
          albumId={coverTarget.id}
          albumName={coverTarget.albumName}
          onClose={() => setCoverTarget(null)}
        />
      )}
    </div>
  );
}

/**
 * Grid/list layout switch. Same native-radios-under-CSS construction as
 * `MediaTypeFilter` — arrow keys and group announcement come from the
 * platform, the segmented look from Tailwind.
 */
function ViewToggle({ view, onChange }: { view: AlbumsView; onChange: (view: AlbumsView) => void }) {
  const options: { value: AlbumsView; label: string; icon: ReactNode }[] = [
    {
      value: 'grid',
      label: 'Grid view',
      icon: (
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden>
          <rect x="3" y="3" width="7" height="7" rx="1" />
          <rect x="14" y="3" width="7" height="7" rx="1" />
          <rect x="3" y="14" width="7" height="7" rx="1" />
          <rect x="14" y="14" width="7" height="7" rx="1" />
        </svg>
      ),
    },
    {
      value: 'list',
      label: 'List view',
      icon: (
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden>
          <path d="M4 6h16M4 12h16M4 18h16" />
        </svg>
      ),
    },
  ];

  return (
    <fieldset className="inline-flex items-center rounded-lg border border-line bg-surface p-0.5" aria-label="Layout">
      {options.map((option) => {
        const checked = option.value === view;
        return (
          <label
            key={option.value}
            title={option.label}
            className={`cursor-pointer rounded-md px-2 py-1.5 transition-colors ${
              checked ? 'bg-accent text-ink-inverse' : 'text-ink-muted hover:text-ink'
            }`}
          >
            <input
              type="radio"
              name="albums-view"
              value={option.value}
              checked={checked}
              onChange={() => onChange(option.value)}
              className="sr-only"
              aria-label={option.label}
            />
            {option.icon}
          </label>
        );
      })}
    </fieldset>
  );
}

/**
 * The virtualised scroll area, for both views.
 *
 * `buildRenderList` has already flattened grouping, collapsing and the choice
 * of view into one sequence of fixed-height items, so this component runs a
 * SINGLE virtualiser over that sequence and switches on `item.kind` when
 * rendering. No "grouped grid" branch, no separate list implementation.
 *
 * Collapsed years are deliberately component state, not a preference: they
 * reset on navigation, which matches what collapsing means — "skip this while
 * I scan today", not "hide 2019 forever" (see prefs.ts for the fuller
 * argument).
 */
function AlbumsScroller({
  albums,
  view,
  groupOrder,
  canPickCover,
  suggestionSize,
  selectedIds,
  overlap,
  onToggleSelected,
  onPickCover,
}: {
  albums: AlbumResponseDto[];
  view: AlbumsView;
  groupOrder: 'asc' | 'desc' | null;
  canPickCover: boolean;
  suggestionSize: number;
  selectedIds: ReadonlySet<string>;
  overlap: Map<string, OverlapEntry>;
  onToggleSelected: (albumId: string) => void;
  onPickCover: (album: AlbumResponseDto) => void;
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const [columns, setColumns] = useState(4);
  const [rowHeight, setRowHeight] = useState(240);
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());

  const measure = (element: HTMLDivElement | null) => {
    if (!element) return;
    const width = element.clientWidth - 32;
    const next = Math.max(1, Math.floor(width / CARD_MIN_WIDTH));
    const cardWidth = (width - ROW_GAP * (next - 1)) / next;
    setColumns(next);
    // Square thumbnail plus a two-line caption.
    setRowHeight(cardWidth + 54 + ROW_GAP);
  };

  const items = useMemo(
    () => buildRenderList(albums, { view, columns, groupOrder, collapsed }),
    [albums, view, columns, groupOrder, collapsed],
  );

  const virtualizer = useVirtualizer({
    count: items.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: (index) => {
      const item = items[index];
      if (!item) return rowHeight;
      if (item.kind === 'header') return HEADER_HEIGHT;
      if (item.kind === 'list-row') return LIST_ROW_HEIGHT;
      return rowHeight;
    },
    // The precomputed keys from buildRenderList: stable while filtering and
    // collapsing shuffle indexes, which keeps scroll position sane.
    getItemKey: (index) => items[index]?.key ?? index,
    overscan: 4,
  });

  // The virtualiser caches sizes by item key, and a grid row keeps its key
  // across a resize or view switch that changes its height — so remeasure
  // whenever the geometry inputs move.
  useEffect(() => {
    virtualizer.measure();
  }, [virtualizer, view, columns, rowHeight, groupOrder]);

  const toggleYear = (label: string) => {
    setCollapsed((previous) => {
      const next = new Set(previous);
      if (next.has(label)) next.delete(label);
      else next.add(label);
      return next;
    });
  };

  if (albums.length === 0) {
    return <p className="p-8 text-center text-ink-muted">No albums match those filters.</p>;
  }

  return (
    <div
      ref={(element) => {
        scrollRef.current = element;
        measure(element);
      }}
      className="scrollbar-subtle min-h-0 flex-1 overflow-y-auto px-4 py-4"
    >
      <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
        {virtualizer.getVirtualItems().map((row) => {
          const item = items[row.index];
          if (!item) return null;

          if (item.kind === 'header') {
            return (
              <div
                key={row.key}
                className="absolute left-0 right-0"
                style={{ transform: `translateY(${row.start}px)`, height: HEADER_HEIGHT }}
              >
                <button
                  type="button"
                  onClick={() => toggleYear(item.label)}
                  aria-expanded={!item.collapsed}
                  className="flex h-full w-full items-center gap-2 text-left"
                >
                  <svg
                    width="14"
                    height="14"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2"
                    aria-hidden
                    className={`text-ink-faint transition-transform ${item.collapsed ? '-rotate-90' : ''}`}
                    style={{ transitionTimingFunction: 'var(--ease-out-soft)' }}
                  >
                    <path d="m6 9 6 6 6-6" />
                  </svg>
                  <span className="text-sm font-semibold text-ink">{item.label}</span>
                  <span className="text-xs tabular-nums text-ink-faint">
                    {item.count.toLocaleString()} {item.count === 1 ? 'album' : 'albums'}
                  </span>
                </button>
              </div>
            );
          }

          if (item.kind === 'list-row') {
            return (
              <div
                key={row.key}
                className="absolute left-0 right-0"
                style={{ transform: `translateY(${row.start}px)`, height: LIST_ROW_HEIGHT }}
              >
                <AlbumListRow
                  album={item.album}
                  overlap={overlap.get(item.album.id) ?? null}
                  isSelected={selectedIds.has(item.album.id)}
                  onToggleSelected={() => onToggleSelected(item.album.id)}
                />
              </div>
            );
          }

          return (
            <div
              key={row.key}
              className="absolute left-0 right-0 grid"
              style={{
                transform: `translateY(${row.start}px)`,
                height: rowHeight,
                gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))`,
                gap: ROW_GAP,
              }}
            >
              {item.albums.map((album) => (
                <AlbumCard
                  key={album.id}
                  album={album}
                  canPickCover={canPickCover}
                  suggestionSize={suggestionSize}
                  isSelected={selectedIds.has(album.id)}
                  overlap={overlap.get(album.id) ?? null}
                  onToggleSelected={() => onToggleSelected(album.id)}
                  onPickCover={() => onPickCover(album)}
                />
              ))}
            </div>
          );
        })}
      </div>
    </div>
  );
}

/** Month + year for the list view, from the album's effective date. */
const listDateFormat = new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short' });

function albumDateLabel(album: AlbumResponseDto): string {
  const ms = albumDateMs(album);
  return ms === null ? '—' : listDateFormat.format(ms);
}

/**
 * A compact list row: name, count, date. Fixed height, because the
 * virtualiser positions rows by arithmetic — a row that grew would overlap
 * its neighbour.
 */
function AlbumListRow({
  album,
  overlap,
  isSelected,
  onToggleSelected,
}: {
  album: AlbumResponseDto;
  overlap: OverlapEntry | null;
  isSelected: boolean;
  onToggleSelected: () => void;
}) {
  return (
    <div className="group/row flex h-full items-center gap-2 border-b border-line-soft pl-2">
      {/* The list view shipped without this, which quietly made merging and
          moving GRID-ONLY features — the bar can only act on a selection, and
          there was no way to make one here. Unlike the grid's overlay
          checkbox this one holds its own column, so the row text never shifts
          when it appears. A sibling of the Link, because a button inside an
          anchor is invalid HTML. */}
      <button
        type="button"
        onClick={onToggleSelected}
        role="checkbox"
        aria-checked={isSelected}
        aria-label={`Select “${album.albumName}”`}
        className={`grid h-5 w-5 shrink-0 place-items-center rounded border transition-opacity focus-visible:opacity-100 group-hover/row:opacity-100 ${
          isSelected
            ? 'border-accent bg-accent text-ink-inverse opacity-100'
            : 'border-line text-transparent opacity-0'
        }`}
      >
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3" aria-hidden>
          <path d="m5 13 4 4L19 7" />
        </svg>
      </button>

    <Link
      to="/albums/$albumId"
      params={{ albumId: album.id }}
      className="flex h-full min-w-0 flex-1 items-center gap-4 rounded-md px-2 transition-colors hover:bg-surface focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
    >
      <span className="min-w-0 flex-1 truncate text-sm font-medium text-ink" title={album.albumName}>
        {album.albumName}
      </span>
      {overlap && <OverlapBadge entry={overlap} variant="row" />}
      <span className="w-24 shrink-0 text-right text-xs tabular-nums text-ink-faint">
        {album.assetCount.toLocaleString()} {album.assetCount === 1 ? 'item' : 'items'}
      </span>
      <span className="w-24 shrink-0 text-right text-xs tabular-nums text-ink-faint">
        {albumDateLabel(album)}
      </span>
    </Link>
    </div>
  );
}

/**
 * One album card.
 *
 * The card used to be a single `<Link>`; the pick-cover affordance forced a
 * restructure, because a button inside an anchor is invalid HTML and worse
 * for assistive tech. Now a relative wrapper holds the Link and the button as
 * SIBLINGS, with named groups (`group/card`, Tailwind v4) so hover styling
 * still spans the whole card while focus styling tracks the actual link.
 */
function AlbumCard({
  album,
  canPickCover,
  suggestionSize,
  isSelected,
  overlap,
  onToggleSelected,
  onPickCover,
}: {
  album: AlbumResponseDto;
  canPickCover: boolean;
  suggestionSize: number;
  isSelected: boolean;
  overlap: OverlapEntry | null;
  onToggleSelected: () => void;
  onPickCover: () => void;
}) {
  // Hover INTENT, not hover: the candidates flyout fires a sidecar request, so
  // it arms only after the pointer has rested on the card for a beat — sweeping
  // across a grid of 4,878 albums must not fire 4,878 requests. The card's rect
  // is measured at arm time and anchors the fixed-position flyout.
  //
  // Leaving gets a GRACE PERIOD rather than instant dismissal: the flyout is a
  // DOM child of this wrapper, so entering it re-fires mouseenter here, but the
  // pointer may briefly cross a gap between card and panel on the way — 250ms
  // of grace absorbs that without making the panel feel sticky.
  const [flyout, setFlyout] = useState<DOMRect | null>(null);
  const wrapperRef = useRef<HTMLDivElement>(null);
  const armTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const leaveTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const close = () => {
    setFlyout(null);
    if (flyoutOwner === album.id) flyoutOwner = null;
  };

  useEffect(
    () => () => {
      if (armTimer.current !== null) clearTimeout(armTimer.current);
      if (leaveTimer.current !== null) clearTimeout(leaveTimer.current);
      // A card can unmount mid-hover: the virtualiser recycles rows as the grid
      // scrolls. Releasing ownership here stops a departed card from locking
      // every other one out of ever opening.
      if (flyoutOwner === album.id) flyoutOwner = null;
    },
    [album.id],
  );

  const onEnter = () => {
    if (leaveTimer.current !== null) {
      clearTimeout(leaveTimer.current);
      leaveTimer.current = null;
    }
    if (!canPickCover || flyout !== null || armTimer.current !== null) return;
    // Another card's panel is up and the pointer is merely passing over this
    // one on its way there. Do not steal it.
    if (flyoutOwner !== null && flyoutOwner !== album.id) return;

    armTimer.current = setTimeout(() => {
      armTimer.current = null;
      const rect = wrapperRef.current?.getBoundingClientRect();
      if (!rect) return;
      flyoutOwner = album.id;
      setFlyout(rect);
    }, 300);
  };

  const onLeave = () => {
    if (armTimer.current !== null) {
      clearTimeout(armTimer.current);
      armTimer.current = null;
    }
    if (leaveTimer.current === null) {
      // Long enough to cross the grid to a panel pinned at the top of the
      // viewport, which is a much longer journey than the old above-the-card
      // placement asked for.
      leaveTimer.current = setTimeout(() => {
        leaveTimer.current = null;
        close();
      }, 600);
    }
  };

  return (
    <div
      ref={wrapperRef}
      className="group/card relative min-w-0"
      onMouseEnter={onEnter}
      onMouseLeave={onLeave}
    >
      <Link
        to="/albums/$albumId"
        params={{ albumId: album.id }}
        className="group/link flex min-w-0 flex-col gap-2 focus-visible:outline-none"
      >
        <div
          className={`relative aspect-square overflow-hidden rounded-lg bg-raised ring-1 transition-all duration-200 group-focus-visible/link:ring-2 group-focus-visible/link:ring-accent ${
            isSelected ? 'ring-2 ring-accent' : 'ring-line group-hover/card:ring-accent'
          }`}
        >
          {album.albumThumbnailAssetId ? (
            <img
              src={assetThumbnailUrl(album.albumThumbnailAssetId)}
              alt=""
              loading="lazy"
              decoding="async"
              className="h-full w-full object-cover transition-transform duration-300 group-hover/card:scale-[1.03]"
              style={{ transitionTimingFunction: 'var(--ease-out-soft)' }}
            />
          ) : (
            <div className="grid h-full w-full place-items-center text-ink-faint" aria-hidden>
              <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
                <rect x="3" y="3" width="18" height="18" rx="2" />
                <path d="m21 15-5-5L5 21" />
              </svg>
            </div>
          )}

          {overlap && <OverlapBadge entry={overlap} variant="card" />}
        </div>

        <div className="min-w-0">
          <p className="truncate text-sm font-medium text-ink" title={album.albumName}>
            {album.albumName}
          </p>
          <p className="text-xs tabular-nums text-ink-faint">
            {album.assetCount.toLocaleString()} {album.assetCount === 1 ? 'item' : 'items'}
          </p>
        </div>
      </Link>

      {/* Selection checkbox. Shown on hover, on keyboard focus, and always
          once ticked — an invisible checkbox on a selected card would make the
          selection impossible to undo without the bar. A sibling of the Link
          rather than a child, because interactive elements do not nest. */}
      <button
        type="button"
        onClick={onToggleSelected}
        role="checkbox"
        aria-checked={isSelected}
        aria-label={`Select “${album.albumName}” for merging`}
        className={`absolute left-2 top-2 z-[60] grid h-6 w-6 place-items-center rounded-md border transition-opacity focus-visible:opacity-100 group-hover/card:opacity-100 ${
          isSelected
            ? 'border-accent bg-accent text-ink-inverse opacity-100'
            : 'border-white/60 bg-black/50 text-transparent opacity-0'
        }`}
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3" aria-hidden>
          <path d="m5 13 4 4L19 7" />
        </svg>
      </button>

      {/* The signature interaction carried over from the old injected UI:
          rest on a card and its five best cover candidates appear at a
          judgeable size; click one and the cover is set (and locked) on the
          spot. */}
      {flyout && (
        <HoverCandidates
          albumId={album.id}
          currentCoverId={album.albumThumbnailAssetId ?? null}
          anchor={flyout}
          thumbSize={suggestionSize}
          onMore={onPickCover}
          // The panel lives in a portal, outside this wrapper's DOM subtree,
          // so it reports its own hover. Entering it cancels the grace timer
          // (onEnter arms nothing while the flyout is up); leaving restarts it.
          onHoverChange={(inside) => (inside ? onEnter() : onLeave())}
        />
      )}

      {/* Hidden until hover or keyboard focus; not rendered at all when the
          sidecar is down. Absent, not broken. */}
      {canPickCover && (
        <button
          type="button"
          onClick={onPickCover}
          aria-label={`Pick a cover for ${album.albumName}`}
          title="Pick cover"
          className="absolute right-2 top-2 z-10 rounded-md bg-black/60 p-1.5 text-white opacity-0 transition-opacity hover:bg-black/80 focus-visible:opacity-100 group-hover/card:opacity-100"
        >
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden>
            <rect x="3" y="3" width="18" height="18" rx="2" />
            <circle cx="9" cy="9" r="2" />
            <path d="m21 15-5-5L5 21" />
          </svg>
        </button>
      )}
    </div>
  );
}

/**
 * The "new album" dialog.
 *
 * On success it navigates INTO the new album rather than staying on the grid:
 * a just-created album is empty, and the detail page is where doing something
 * about that (searching, adding) can start — it also confirms the creation
 * better than a toast would. Escape handling and the scroll lock mirror
 * CoverPickerModal so every overlay in the app behaves identically.
 */
function NewAlbumDialog({ onClose }: { onClose: () => void }) {
  const create = useCreateAlbum();
  const navigate = useNavigate();
  const [name, setName] = useState('');

  // Duplicate names are LEGAL in Immich but BANNED here. The folder-to-album
  // creators already own an album per library folder, so a name collision
  // produces two identical-looking cards and photos split silently between
  // them — which is exactly what happened before 14 duplicate albums had to be
  // merged by hand. The dialog therefore refuses the name and offers the
  // existing album instead; the same find-or-create rule governs folder drops
  // (see features/upload/UploadButton.tsx).
  const existing = useAlbums().data;
  const clash = useMemo(() => {
    const needle = name.trim().toLowerCase();
    if (!needle) return null;
    return existing?.find((album) => album.albumName.toLowerCase() === needle) ?? null;
  }, [existing, name]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      window.removeEventListener('keydown', onKey);
      document.body.style.overflow = previousOverflow;
    };
  }, [onClose]);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const albumName = name.trim();
    if (!albumName || create.isPending || clash) return;

    create.mutate(albumName, {
      onSuccess: (album) => {
        onClose();
        void navigate({ to: '/albums/$albumId', params: { albumId: album.id } });
      },
    });
  };

  return (
    <div className="fixed inset-0 z-50 grid place-items-center p-4">
      <button
        type="button"
        aria-label="Close new album dialog"
        className="absolute inset-0 bg-black/60"
        onClick={onClose}
      />

      <form
        role="dialog"
        aria-modal="true"
        aria-label="Create a new album"
        onSubmit={submit}
        className="relative w-full max-w-sm rounded-lg border border-line bg-overlay p-4 shadow-2xl"
      >
        <h2 className="text-base font-semibold text-ink">New album</h2>

        <input
          autoFocus
          type="text"
          value={name}
          onChange={(event) => setName(event.target.value)}
          placeholder="Album name"
          aria-label="Album name"
          className="mt-3 w-full rounded-md border border-line bg-surface px-3 py-2 text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none"
        />

        {clash && (
          <p className="mt-2 rounded-md bg-raised px-2.5 py-2 text-xs text-ink-muted">
            “{clash.albumName}” already exists with{' '}
            <span className="tabular-nums text-ink">{clash.assetCount.toLocaleString()}</span>{' '}
            {clash.assetCount === 1 ? 'item' : 'items'}. One name, one album —{' '}
            <button
              type="button"
              onClick={() => {
                onClose();
                void navigate({ to: '/albums/$albumId', params: { albumId: clash.id } });
              }}
              className="font-medium text-accent underline-offset-2 hover:underline"
            >
              open it instead
            </button>
            .
          </p>
        )}

        {create.isError && (
          <p className="mt-2 text-xs text-danger">
            Could not create the album: {(create.error as Error).message}
          </p>
        )}

        <div className="mt-4 flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted transition-colors hover:bg-raised hover:text-ink"
          >
            Cancel
          </button>
          <button
            type="submit"
            disabled={!name.trim() || create.isPending || clash !== null}
            className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-ink-inverse transition-colors hover:bg-accent-hover disabled:opacity-50"
          >
            {create.isPending ? 'Creating…' : 'Create'}
          </button>
        </div>
      </form>
    </div>
  );
}

function AlbumsSkeleton() {
  return (
    <div className="grid grid-cols-2 gap-5 p-4 sm:grid-cols-3 lg:grid-cols-5">
      {Array.from({ length: 15 }, (_, index) => (
        <div key={index}>
          <div
            className="aspect-square animate-pulse rounded-lg bg-raised"
            style={{ animationDelay: `${index * 35}ms` }}
          />
          <div className="mt-2 h-3.5 w-3/4 animate-pulse rounded bg-raised" />
        </div>
      ))}
    </div>
  );
}
