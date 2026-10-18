import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useTimeBucketAssets, useTimeBuckets, usePrefetchBucket } from '@/shared/api/queries';
import { assetThumbnailUrl } from '@/shared/api/client';
import type { TimeBucketQuery, TimelineAsset } from '@/shared/api/timeline';
import { estimateBucketHeight, layoutAssets, type LayoutOptions } from './layout';
import { ProgressiveImage } from '@/shared/ui/Thumbhash';
import { useSelectionOptional, type SelectionContextValue } from '@/features/selection/SelectionContext';

/**
 * The timeline.
 *
 * This is the component the whole architecture exists to make possible: 1.6M
 * assets, scrollable, on a personal machine.
 *
 * Three ideas working together:
 *
 *   1. BUCKETS. The server describes the library as one row per month. A single
 *      small response gives us every month and its count, so we know the total
 *      scroll height before fetching any photos.
 *
 *   2. VIRTUALISATION. Only the months near the viewport are mounted. Scrolling
 *      to 2019 does not render 2020–2026 first.
 *
 *   3. LAZY BUCKETS. Each visible month fetches its own assets, and TanStack
 *      Query caches them. Scrolling back is instant.
 *
 * The naive alternative — fetch all assets, render them all — would transfer
 * hundreds of megabytes and lock the tab. It is worth understanding why this is
 * shaped the way it is rather than treating it as ceremony.
 */

const TARGET_ROW_HEIGHT = 220;
const GAP = 4;
/** Months to render beyond the viewport. Higher trades memory for smoothness. */
const OVERSCAN = 2;

export interface TimelineGridProps {
  query: TimeBucketQuery;
  onSelect: (asset: TimelineAsset, all: TimelineAsset[]) => void;
  /** What an empty grid shows: words, or words and the buttons that fill it. */
  emptyMessage?: React.ReactNode;
}

export function TimelineGrid({ query, onSelect, emptyMessage }: TimelineGridProps) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(0);

  const { data: buckets, isPending, isError, error } = useTimeBuckets(query);
  const prefetch = usePrefetchBucket(query);

  /*
   * Track container width so the layout can justify rows.
   *
   * A callback ref rather than `useEffect`, and it measures SYNCHRONOUSLY on
   * attach rather than waiting for the observer's first callback. Relying on
   * ResizeObserver alone leaves width at 0 through the first paint, and since
   * `layoutAssets` bails out at width <= 0, the grid renders month headers with
   * no photos under them — data present, nothing drawn.
   *
   * ResizeObserver still handles subsequent changes: the sidebar can resize us
   * without the window resizing at all, so a window listener would miss it.
   */
  const observerRef = useRef<ResizeObserver | null>(null);

  const attachScroller = useCallback((element: HTMLDivElement | null) => {
    observerRef.current?.disconnect();
    scrollRef.current = element;

    if (!element) return;

    setWidth(element.clientWidth);

    const observer = new ResizeObserver(([entry]) => {
      if (entry) setWidth(entry.contentRect.width);
    });
    observer.observe(element);
    observerRef.current = observer;
  }, []);

  useEffect(() => () => observerRef.current?.disconnect(), []);

  const options: LayoutOptions = useMemo(
    () => ({ containerWidth: rowWidth(width), targetRowHeight: TARGET_ROW_HEIGHT, gap: GAP }),
    [width],
  );

  const virtualizer = useVirtualizer({
    count: buckets?.length ?? 0,
    getScrollElement: () => scrollRef.current,
    // Estimated from the count alone, so the scrollbar is honest before any
    // bucket loads. Real heights replace these via `measureElement`.
    estimateSize: (index) => {
      const bucket = buckets?.[index];
      if (!bucket) return TARGET_ROW_HEIGHT;
      return estimateBucketHeight(bucket.count, options) + MONTH_HEADER_HEIGHT;
    },
    overscan: OVERSCAN,
  });

  // Scroll position drives row-level windowing inside each month (see
  // BucketBlock). Read from the virtualiser rather than a scroll listener so
  // there is one source of truth and no duplicate event handling.
  const scrollOffset = virtualizer.scrollOffset ?? 0;
  const viewportHeight = scrollRef.current?.clientHeight ?? 800;

  // Prefetch the months just past the rendered window so their assets are
  // already arriving before they scroll into view.
  const items = virtualizer.getVirtualItems();
  useEffect(() => {
    if (!buckets) return;
    const last = items.at(-1);
    if (!last) return;

    for (let i = last.index + 1; i <= last.index + OVERSCAN && i < buckets.length; i++) {
      const next = buckets[i];
      if (next) prefetch(next.timeBucket);
    }
  }, [items, buckets, prefetch]);

  /*
   * The scroll container renders UNCONDITIONALLY, with loading and empty
   * states nested inside it.
   *
   * This matters more than it looks. Early-returning a skeleton before this
   * div mounts means `scrollRef.current` is null when the virtualiser first
   * initialises, and it never acquires a scroll element afterwards — so
   * `getVirtualItems()` stays empty and nothing renders even once the data
   * arrives. The container is layout, not data, so it should not be
   * conditional on data.
   */
  const empty = !isPending && !isError && (!buckets || buckets.length === 0);

  return (
    <div ref={attachScroller} className="scrollbar-subtle h-full overflow-y-auto overflow-x-hidden">
      {isPending && <GridSkeleton />}

      {isError && (
        <div className="flex h-full items-center justify-center p-8 text-center">
          <div>
            <p className="text-ink">Could not load the timeline.</p>
            <p className="mt-1 text-sm text-ink-faint">{(error as Error)?.message}</p>
          </div>
        </div>
      )}

      {empty && (
        <div className="flex h-full items-center justify-center p-8 text-center text-ink-muted">
          {emptyMessage ?? 'Nothing here yet.'}
        </div>
      )}

      <div style={{ height: virtualizer.getTotalSize(), position: 'relative', width: '100%' }}>
        {items.map((item) => {
          const bucket = buckets?.[item.index];
          if (!bucket) return null;

          return (
            <div
              key={bucket.timeBucket}
              data-index={item.index}
              ref={virtualizer.measureElement}
              style={bucketItemStyle(item.start)}
            >
              <BucketBlock
                bucket={bucket.timeBucket}
                count={bucket.count}
                query={query}
                options={options}
                onSelect={onSelect}
                blockTop={item.start}
                scrollTop={scrollOffset}
                viewportHeight={viewportHeight}
              />
            </div>
          );
        })}
      </div>
    </div>
  );
}

const MONTH_HEADER_HEIGHT = 52;

/** Each month's side padding, in pixels. The rows are laid out inside it. */
const SECTION_PADDING_X = 16;

/**
 * The width the justified rows fill: the scroller's width less the month's
 * padding on both sides. Laying the rows out at the full scroller width made
 * every row 32 px too wide, so the last picture of each row lost 16 px under
 * the right edge and the right gutter vanished (Windows UI test, 2026-10-16).
 */
export function rowWidth(scrollerWidth: number): number {
  return Math.max(0, scrollerWidth - 2 * SECTION_PADDING_X);
}

/**
 * Where one month sits in the virtual list: `top`, not a transform. The
 * month header is `position: sticky`, and sticky offsets are worked out from
 * layout, which a transform does not move: under `translateY(start)` every
 * header but the first stuck `start` pixels below the top of the view,
 * floating over the pictures (Windows UI test, 2026-10-16).
 */
export function bucketItemStyle(start: number): React.CSSProperties {
  return { position: 'absolute', top: start, left: 0, width: '100%' };
}

function BucketBlock({
  bucket,
  count,
  query,
  options,
  onSelect,
  blockTop,
  scrollTop,
  viewportHeight,
}: {
  bucket: string;
  count: number;
  query: TimeBucketQuery;
  options: LayoutOptions;
  onSelect: (asset: TimelineAsset, all: TimelineAsset[]) => void;
  /** This month's offset within the scroll container. */
  blockTop: number;
  scrollTop: number;
  viewportHeight: number;
}) {
  const { data: assets } = useTimeBucketAssets(bucket, query);

  /*
   * Selection is OPTIONAL by design. Pages that mount a `SelectionProvider`
   * (Photos, Trash) get hover checkboxes and click-to-toggle; pages that do
   * not (People, Albums today) get the plain grid, from the same component.
   * Read here rather than in `TimelineGrid` so the null case costs nothing
   * but a context lookup.
   */
  const selection = useSelectionOptional();

  const layout = useMemo(
    () => (assets ? layoutAssets(assets, options) : null),
    [assets, options],
  );

  /*
   * Row-level windowing.
   *
   * The outer virtualiser works per MONTH. That is not enough on its own: one
   * month here holds 3,355 assets and one album holds 183,075, so a single
   * mounted month would put tens of thousands of nodes in the DOM and stall
   * scrolling.
   *
   * The justified layout already knows every row's y offset, so narrowing to
   * the rows that intersect the viewport is a filter, not a second virtualiser.
   * OVERSCAN_PX above and below keeps fast scrolling from showing gaps.
   */
  const visibleRows = useMemo(() => {
    if (!layout) return [];

    const OVERSCAN_PX = 600;
    // Header sits above the rows inside this block.
    const rowsTop = blockTop + MONTH_HEADER_HEIGHT;
    const from = scrollTop - rowsTop - OVERSCAN_PX;
    const to = scrollTop - rowsTop + viewportHeight + OVERSCAN_PX;

    const result: { row: (typeof layout.rows)[number]; rowIndex: number }[] = [];
    for (const [rowIndex, row] of layout.rows.entries()) {
      const rowBottom = row.y + row.height;
      if (rowBottom >= from && row.y <= to) result.push({ row, rowIndex });
    }
    return result;
  }, [layout, blockTop, scrollTop, viewportHeight]);

  const label = useMemo(() => formatMonth(bucket), [bucket]);

  return (
    <section className="pb-6" style={{ paddingInline: SECTION_PADDING_X }}>
      <header
        className="sticky top-0 z-10 flex items-baseline gap-3 bg-ground/85 py-3 backdrop-blur"
        style={{ height: MONTH_HEADER_HEIGHT }}
      >
        <h2 className="text-lg font-semibold tracking-tight text-ink">{label}</h2>
        <span className="text-sm text-ink-faint">{count.toLocaleString()}</span>
      </header>

      {layout ? (
        <div style={{ position: 'relative', height: layout.totalHeight }}>
          {visibleRows.map(({ row, rowIndex }) => (
            <div key={rowIndex} style={{ position: 'absolute', top: row.y, left: 0, right: 0, height: row.height }}>
              {row.tiles.map((tile) => (
                <Tile
                  key={tile.asset.id}
                  tile={tile}
                  onOpen={() => onSelect(tile.asset, assets ?? [])}
                  selection={selection}
                  bucket={bucket}
                  bucketAssets={assets ?? EMPTY_ASSETS}
                />
              ))}
            </div>
          ))}
        </div>
      ) : (
        <div
          className="animate-pulse rounded-md bg-raised/40"
          style={{ height: estimateBucketHeight(count, options) }}
        />
      )}
    </section>
  );
}

const EMPTY_ASSETS: TimelineAsset[] = [];

/**
 * One tile: a photo, and — when a selection provider is present — a checkbox.
 *
 * The click contract, in order of precedence:
 *
 *   - Selection ACTIVE (something already ticked): any click on the tile
 *     toggles it; shift-click extends a range from the last plain click.
 *     Ranges only reach within one month bucket — assets are fetched per
 *     bucket, so a cross-bucket range would need data the client never
 *     downloaded. A cross-bucket shift-click degrades to a toggle.
 *   - Selection EMPTY: clicking the photo opens the viewer, exactly as on a
 *     page with no provider at all; clicking the hover checkbox is what
 *     ENTERS selection mode.
 *
 * Structurally the tile is a wrapper `div` holding two sibling buttons, not
 * a checkbox nested inside the photo button — nested interactive elements
 * are invalid HTML and screen readers rightly refuse to guess at them.
 */
function Tile({
  tile,
  onOpen,
  selection,
  bucket,
  bucketAssets,
}: {
  tile: { asset: TimelineAsset; x: number; width: number; height: number };
  onOpen: () => void;
  selection: SelectionContextValue | null;
  bucket: string;
  bucketAssets: TimelineAsset[];
}) {
  const { asset, x, width, height } = tile;

  const isSelected = selection?.isSelected(asset.id) ?? false;

  const toggleFrom = (event: React.MouseEvent) => {
    if (!selection) return;
    if (event.shiftKey) {
      selection.selectRange(asset, bucket, bucketAssets);
    } else {
      selection.toggle(asset, bucket);
    }
  };

  return (
    <div
      className="group absolute select-none hover:z-10 focus-within:z-10"
      style={{ left: x, width, height }}
    >
      <button
        type="button"
        onClick={(event) => {
          if (selection?.active) {
            toggleFrom(event);
          } else {
            onOpen();
          }
        }}
        className="absolute inset-0 overflow-hidden rounded-sm bg-raised focus-visible:z-10"
        aria-label={asset.isImage ? 'Photo' : 'Video'}
        aria-pressed={selection?.active ? isSelected : undefined}
      >
        {/* Shrinking the photo inside its frame is the selected state — the
            revealed ground reads as "picked up" without hiding any pixels
            behind an overlay. */}
        <span
          className="absolute inset-0 block overflow-hidden rounded-sm transition-transform duration-200"
          style={{
            transform: isSelected ? 'scale(0.9)' : undefined,
            transitionTimingFunction: 'var(--ease-out-soft)',
          }}
        >
          <ProgressiveImage
            src={assetThumbnailUrl(asset.id)}
            hash={asset.thumbhash}
            alt=""
            className="h-full w-full"
          />

          {/* Hover wash. Subtle enough not to fight the photograph. */}
          <span className="pointer-events-none absolute inset-0 bg-black/0 transition-colors duration-200 group-hover:bg-black/15" />

          {!asset.isImage && <VideoBadge durationMs={asset.durationMs} />}
          {/* Corner allocation: checkbox top-left, duration bottom-right, so
              the favorite star lives bottom-left where nothing collides. */}
          {asset.isFavorite && (
            <span className="pointer-events-none absolute bottom-1.5 left-1.5 text-accent drop-shadow" aria-hidden>
              ★
            </span>
          )}
        </span>

        {isSelected && (
          <span className="pointer-events-none absolute inset-0 rounded-sm ring-2 ring-inset ring-accent" aria-hidden />
        )}
      </button>

      {selection && (
        <button
          type="button"
          onClick={toggleFrom}
          aria-label={isSelected ? 'Deselect' : 'Select'}
          aria-pressed={isSelected}
          className={`absolute left-1.5 top-1.5 z-10 grid h-6 w-6 place-items-center rounded-full transition-opacity duration-150 ${
            isSelected
              ? 'bg-accent text-ink-inverse opacity-100'
              : `bg-black/45 text-white ring-1 ring-white/70 hover:bg-black/65 ${
                  selection.active ? 'opacity-100' : 'opacity-0 focus-visible:opacity-100 group-hover:opacity-100'
                }`
          }`}
        >
          <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
            <path d="m5 13 4 4L19 7" />
          </svg>
        </button>
      )}
    </div>
  );
}

function VideoBadge({ durationMs }: { durationMs: number | null }) {
  return (
    <span className="pointer-events-none absolute bottom-1.5 right-1.5 rounded bg-black/65 px-1.5 py-0.5 text-[11px] font-medium tabular-nums text-white">
      {formatDuration(durationMs)}
    </span>
  );
}

function GridSkeleton() {
  return (
    <div className="space-y-6 p-4">
      {[0, 1, 2].map((row) => (
        <div key={row}>
          <div className="mb-3 h-5 w-40 animate-pulse rounded bg-raised" />
          <div className="flex gap-1">
            {[0, 1, 2, 3, 4].map((tile) => (
              <div
                key={tile}
                className="h-[220px] flex-1 animate-pulse rounded-sm bg-raised"
                style={{ animationDelay: `${(row * 5 + tile) * 40}ms` }}
              />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

function formatMonth(bucket: string): string {
  const date = new Date(`${bucket}T00:00:00`);
  if (Number.isNaN(date.getTime())) return bucket;
  return date.toLocaleDateString(undefined, { month: 'long', year: 'numeric' });
}

/**
 * Duration formatting.
 *
 * v3 changed `duration` from an "HH:MM:SS.mmm" string to integer milliseconds
 * (see docs/v3-parity-report.md in the setup repo). This app targets v3 and
 * treats it as a number throughout.
 */
export function formatDuration(ms: number | null): string {
  if (ms === null || !Number.isFinite(ms) || ms <= 0) return '';

  const totalSeconds = Math.round(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  const pad = (n: number) => String(n).padStart(2, '0');
  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(seconds)}` : `${minutes}:${pad(seconds)}`;
}
