import { useDeferredValue, useEffect, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { Link, useNavigate } from '@tanstack/react-router';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useAlbums } from '@/shared/api/queries';
import { assetThumbnailUrl } from '@/shared/api/client';
import { apiErrorMessage } from '@/shared/api/tessera';
import type { AlbumDto } from '@/shared/api/types';
import { CoverPicker } from './CoverPicker';
import { OverlapBadge } from './OverlapBadge';
import { useAlbumOverlap, type OverlapEntry } from './overlap';
import { albumDateMs, buildRenderList, sortAlbums, type AlbumSort, type AlbumsView } from './organize';
import { useAlbumsViewPrefs } from './prefs';
import { useCreateAlbum } from './api';

/**
 * Album browser.
 *
 * Folders under a chosen root are albums and stay in sync with the disk;
 * manual albums sit beside them. The list is one request (`assetCount` comes
 * with it), filtering is in memory, and the page virtualises ONE list
 * regardless of view or grouping, so a library of thousands of folders
 * scrolls as one continuous grid.
 *
 * The page itself stays thin by design. Sorting, year grouping and the
 * flattened render list are pure functions in `organize.ts`; the durable view
 * preferences live in `prefs.ts`; the mutations in `api.ts`. What remains here
 * is the part only a component can do: hold the ephemeral state and map data
 * to elements.
 */

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
  const { data: albums, isPending, isError, error } = useAlbums();
  const [prefs, updatePrefs] = useAlbumsViewPrefs();

  const [term, setTerm] = useState('');
  const [minCount, setMinCount] = useState<number>(0);
  const [creating, setCreating] = useState(false);
  const [coverTarget, setCoverTarget] = useState<AlbumDto | null>(null);

  // Which of these albums share a name with another. An empty map means
  // either "not loaded yet" or "no duplicate names" - both render identically
  // (no badges, no filter button), which is the correct behaviour for both.
  const overlap = useAlbumOverlap();
  const [sharedOnly, setSharedOnly] = useState(false);

  // Typing filters every album on every keystroke. `useDeferredValue` lets
  // React keep the input responsive and render the filtered grid at lower
  // priority.
  const deferredTerm = useDeferredValue(term);

  const filtered = useMemo(() => {
    if (!albums) return [];
    const needle = deferredTerm.trim().toLowerCase();

    return albums
      .filter((album) => album.assetCount >= minCount)
      .filter((album) => (sharedOnly ? overlap.has(album.id) : true))
      .filter((album) => (needle ? album.albumName.toLowerCase().includes(needle) : true));
  }, [albums, deferredTerm, minCount, sharedOnly, overlap]);

  const sorted = useMemo(() => sortAlbums(filtered, prefs.sort), [filtered, prefs.sort]);

  // Year direction follows the sort: "oldest first" would look broken under
  // descending year headers. Every other sort defaults to newest-year-first.
  const groupOrder = prefs.groupByYear ? (prefs.sort === 'oldest' ? 'asc' : 'desc') : null;

  if (isPending) return <AlbumsSkeleton />;
  if (isError) {
    return (
      <p className="p-8 text-center text-ink-muted">Could not load albums: {apiErrorMessage(error)}</p>
    );
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

        {/* Only offered where it means something: with no duplicate names the
            map is empty, and a filter that can only ever return nothing is
            worse than no filter. */}
        {overlap.size > 0 && (
          <button
            type="button"
            aria-pressed={sharedOnly}
            onClick={() => setSharedOnly((current) => !current)}
            title="Show only albums whose name another album also has"
            className={`rounded-md border px-2.5 py-1.5 text-[13px] font-medium transition-colors ${
              sharedOnly
                ? 'border-accent bg-accent-muted text-ink'
                : 'border-line text-ink-muted hover:text-ink'
            }`}
          >
            Shared names
          </button>
        )}

        <ViewToggle view={prefs.view} onChange={(view) => updatePrefs({ view })} />

        <div className="ml-auto flex items-center gap-3">
          <span className="text-sm tabular-nums text-ink-faint">
            {filtered.length.toLocaleString()} of {albums.length.toLocaleString()}
          </span>

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
        overlap={overlap}
        onPickCover={setCoverTarget}
      />

      {creating && <NewAlbumDialog onClose={() => setCreating(false)} />}

      {coverTarget && <CoverPicker album={coverTarget} onClose={() => setCoverTarget(null)} />}
    </div>
  );
}

/**
 * Grid/list layout switch. Same native-radios-under-CSS construction as
 * `MediaTypeFilter` - arrow keys and group announcement come from the
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
 */
function AlbumsScroller({
  albums,
  view,
  groupOrder,
  overlap,
  onPickCover,
}: {
  albums: AlbumDto[];
  view: AlbumsView;
  groupOrder: 'asc' | 'desc' | null;
  overlap: Map<string, OverlapEntry>;
  onPickCover: (album: AlbumDto) => void;
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
  // across a resize or view switch that changes its height - so remeasure
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
                <AlbumListRow album={item.album} overlap={overlap.get(item.album.id) ?? null} />
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
                  overlap={overlap.get(album.id) ?? null}
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

function albumDateLabel(album: AlbumDto): string {
  const ms = albumDateMs(album);
  return ms === null ? '—' : listDateFormat.format(ms);
}

/**
 * A compact list row: name, count, date. Fixed height, because the
 * virtualiser positions rows by arithmetic - a row that grew would overlap
 * its neighbour.
 */
function AlbumListRow({ album, overlap }: { album: AlbumDto; overlap: OverlapEntry | null }) {
  return (
    <div
      className="flex h-full items-center gap-2 border-b border-line-soft pl-2"
      data-drop-album-id={album.id}
      data-drop-album-name={album.albumName}
    >
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
 * A relative wrapper holds the Link and the pick-cover button as SIBLINGS: a
 * button inside an anchor is invalid HTML and worse for assistive tech. Named
 * groups (`group/card`, Tailwind v4) let hover styling span the whole card
 * while focus styling tracks the actual link.
 */
function AlbumCard({
  album,
  overlap,
  onPickCover,
}: {
  album: AlbumDto;
  overlap: OverlapEntry | null;
  onPickCover: () => void;
}) {
  return (
    <div className="group/card relative min-w-0" data-drop-album-id={album.id} data-drop-album-name={album.albumName}>
      <Link
        to="/albums/$albumId"
        params={{ albumId: album.id }}
        className="group/link flex min-w-0 flex-col gap-2 focus-visible:outline-none"
      >
        <div className="relative aspect-square overflow-hidden rounded-lg bg-raised ring-1 ring-line transition-all duration-200 group-hover/card:ring-accent group-focus-visible/link:ring-2 group-focus-visible/link:ring-accent">
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

      {/* Hidden until hover or keyboard focus. */}
      {album.assetCount > 0 && (
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
 * about that can start. Duplicate names are refused: folders are albums, so a
 * name collision produces two identical-looking cards and pictures split
 * silently between them. The dialog offers the existing album instead.
 */
function NewAlbumDialog({ onClose }: { onClose: () => void }) {
  const create = useCreateAlbum();
  const navigate = useNavigate();
  const [name, setName] = useState('');

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
            {clash.assetCount === 1 ? 'item' : 'items'}. One name, one album -{' '}
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
            Could not create the album: {apiErrorMessage(create.error)}
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
