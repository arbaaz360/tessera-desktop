import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { assetThumbnailUrl } from '@/shared/api/client';
import { AssetViewer } from '@/features/viewer/AssetViewer';
import { useAlbum } from '@/shared/api/queries';
import { assetToTimelineAsset } from '@/shared/api/timeline';
import { FilterBar } from './FilterBar';
import { COLD_START_HINT, COLD_START_HINT_MS, useSearchResults } from './api';
import {
  filtersToUrl,
  countActiveFilters, hasSearchableInput,
  parseFilters,
  type SearchFilters,
} from './searchParams';

/**
 * Search: semantic (CLIP) by default, exact metadata on demand.
 *
 * Immich embeds every asset with a vision-language model, so the default mode
 * searches by MEANING — "sunset over water" finds sunsets that were never
 * labelled. The "Metadata only" toggle switches to exact matching on file
 * names and EXIF, which also works while the ML model is down.
 *
 * All search state lives in the URL: the query, the album scope, and every
 * filter. The route only validates `q` and `albumId`; the filter params ride
 * alongside and are parsed defensively here (see searchParams.ts for why the
 * URL is treated as untrusted input).
 */
export function SearchPage() {
  const { q, albumId } = useSearch({ from: '/authed/search' });
  const rawSearch = useSearch({ strict: false }) as Record<string, unknown>;
  const navigate = useNavigate();
  const [viewerIndex, setViewerIndex] = useState<number | null>(null);

  // Parsing is pure and cheap, but referential stability matters: the filters
  // object is part of the query key, and a new object identity per render
  // would re-run the search on every keystroke elsewhere on the page.
  const filters = useMemo(() => parseFilters(rawSearch), [rawSearch]);

  const setFilters = (next: SearchFilters) => {
    setViewerIndex(null);
    void navigate({
      to: '/search',
      search: { q, albumId, ...filtersToUrl(next) },
    });
  };

  const results = useSearchResults(q, albumId, filters);
  const items = useMemo(
    () => results.data?.pages.flatMap((page) => page.assets.items) ?? [],
    [results.data],
  );
  const total = results.data?.pages[0]?.assets.total;

  const searchable = hasSearchableInput(q, albumId, filters);

  return (
    <div className="flex h-full flex-col">
      <FilterBar filters={filters} onChange={setFilters} />

      {albumId && <AlbumScopeChip albumId={albumId} q={q} filters={filters} />}

      {!searchable ? (
        <EmptyState
          title="Search your library"
          body={
            filters.metadataOnly
              ? 'Type part of a file name, or set a filter — “all videos from 2016” needs no text at all.'
              : 'Describe what you are looking for — “red car at night”, “birthday cake”, “snow”. Results are ranked by meaning, not filename.'
          }
        />
      ) : results.isPending ? (
        <PendingState />
      ) : results.isError ? (
        <EmptyState title="Search failed" body={(results.error as Error)?.message ?? 'Unknown error'} />
      ) : items.length === 0 ? (
        <EmptyState
          title={q ? `Nothing found for “${q}”` : 'Nothing matches these filters'}
          body="Try describing the scene differently, or loosen a filter. Videos cannot appear in a semantic search yet: it is answered by the Foundation index, which holds photographs only."
        />
      ) : (
        <>
          <div className="border-b border-line-soft px-4 py-2.5">
            <p className="text-sm text-ink-muted">
              <span className="tabular-nums text-ink">{(total ?? items.length).toLocaleString()}</span>{' '}
              results{q ? <> for <span className="text-ink">“{q}”</span></> : ' for these filters'}
              {/* Which index answered. Silence here would make a photographs-only
                  result set look like a library that had lost its videos. */}
              {!filters.metadataOnly && !albumId && countActiveFilters(filters) === 0 && (
                <span className="text-ink-faint"> · photos only, from the Foundation index</span>
              )}
            </p>
          </div>

          <div className="scrollbar-subtle min-h-0 flex-1 overflow-y-auto p-4">
            <div className="grid grid-cols-2 gap-1.5 sm:grid-cols-4 lg:grid-cols-6 xl:grid-cols-8">
              {items.map((asset, index) => (
                <button
                  key={asset.id}
                  type="button"
                  onClick={() => setViewerIndex(index)}
                  className="group aspect-square overflow-hidden rounded-sm bg-raised focus-visible:ring-2 focus-visible:ring-accent"
                >
                  <img
                    src={assetThumbnailUrl(asset.id)}
                    alt=""
                    loading="lazy"
                    decoding="async"
                    className="h-full w-full object-cover transition-transform duration-200 group-hover:scale-105"
                    style={{ transitionTimingFunction: 'var(--ease-out-soft)' }}
                  />
                </button>
              ))}
            </div>

            {results.hasNextPage && (
              <div className="flex justify-center py-6">
                <button
                  type="button"
                  onClick={() => void results.fetchNextPage()}
                  disabled={results.isFetchingNextPage}
                  className="rounded-md border border-line bg-surface px-4 py-2 text-sm font-medium text-ink-muted transition-colors hover:bg-raised hover:text-ink disabled:opacity-60"
                >
                  {results.isFetchingNextPage ? 'Loading…' : 'Load more'}
                </button>
              </div>
            )}
          </div>
        </>
      )}

      {viewerIndex !== null && (
        <AssetViewer
          assets={items.map(assetToTimelineAsset)}
          index={viewerIndex}
          onIndexChange={setViewerIndex}
          onClose={() => setViewerIndex(null)}
        />
      )}
    </div>
  );
}

/**
 * The album-scope indicator. Search reached from an album stays scoped to it;
 * the chip both says so and offers the way out — clearing the scope re-runs
 * the same search across the whole library.
 */
function AlbumScopeChip({
  albumId,
  q,
  filters,
}: {
  albumId: string;
  q: string;
  filters: SearchFilters;
}) {
  const { data: album } = useAlbum(albumId);
  const navigate = useNavigate();

  return (
    <div className="flex items-center gap-2 border-b border-line-soft px-4 py-2">
      <span className="inline-flex items-center gap-1.5 rounded-lg border border-accent bg-accent-muted px-2.5 py-1 text-[13px] font-medium text-accent">
        Searching in {album?.albumName ?? 'album'}
        <button
          type="button"
          aria-label="Search the whole library instead"
          onClick={() =>
            void navigate({ to: '/search', search: { q, albumId: undefined, ...filtersToUrl(filters) } })
          }
          className="rounded p-0.5 hover:bg-accent hover:text-ink-inverse"
        >
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" aria-hidden>
            <path d="M6 6l12 12M18 6L6 18" />
          </svg>
        </button>
      </span>
    </div>
  );
}

/**
 * Pending, in two acts. The skeleton alone is right for a warm search (~6s),
 * but a cold start runs 30–60s while the ML model reloads — long enough that
 * an unexplained spinner reads as "broken". After eight seconds the page says
 * what is actually happening.
 */
function PendingState() {
  const [explain, setExplain] = useState(false);

  useEffect(() => {
    const timer = setTimeout(() => setExplain(true), COLD_START_HINT_MS);
    return () => clearTimeout(timer);
  }, []);

  return (
    <div className="min-h-0 flex-1">
      {explain && (
        <p className="border-b border-line-soft px-4 py-2.5 text-sm text-ink-muted" role="status">
          {COLD_START_HINT}
        </p>
      )}
      <div className="grid grid-cols-2 gap-1.5 p-4 sm:grid-cols-4 lg:grid-cols-6 xl:grid-cols-8">
        {Array.from({ length: 24 }, (_, index) => (
          <div
            key={index}
            className="aspect-square animate-pulse rounded-sm bg-raised"
            style={{ animationDelay: `${index * 25}ms` }}
          />
        ))}
      </div>
    </div>
  );
}

function EmptyState({ title, body }: { title: string; body: string }) {
  return (
    <div className="grid min-h-0 flex-1 place-items-center px-6 text-center">
      <div className="max-w-md">
        <h2 className="text-lg font-medium text-ink">{title}</h2>
        <p className="mt-2 text-sm leading-relaxed text-ink-muted">{body}</p>
      </div>
    </div>
  );
}
