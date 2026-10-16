import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useSearch } from '@tanstack/react-router';
import { assetThumbnailUrl } from '@/shared/api/client';
import { apiErrorMessage, isModelNotReady } from '@/shared/api/tessera';
import { useAlbum } from '@/shared/api/queries';
import type { TimelineAsset } from '@/shared/api/timeline';
import { AssetViewer } from '@/features/viewer/AssetViewer';
import { SearchModelProgress } from '@/features/library/SearchModelProgress';
import { FilterBar } from './FilterBar';
import { SEARCH_LIMIT, SEARCH_LIMIT_MAX, useSearchResults, useSimilarResults } from './api';
import {
  filtersToUrl,
  countActiveFilters,
  hasSearchableInput,
  parseFilters,
  type SearchFilters,
} from './searchParams';

/**
 * Search: by meaning by default, by file name on demand, and "more like
 * this" from the viewer.
 *
 * The core embeds every picture with a vision-language model on this
 * machine, so the default mode searches by what is IN the picture - "ink
 * sketch of a horse" finds sketches that were never labelled. The "File name"
 * toggle switches to exact matching, which also works before the model has
 * been downloaded.
 *
 * All search state lives in the URL: the query, the album scope, the picture
 * a "more like this" started from, and every filter.
 */
export function SearchPage() {
  const { q, albumId, similar } = useSearch({ from: '/app/search' });
  const rawSearch = useSearch({ strict: false }) as Record<string, unknown>;
  const navigate = useNavigate();
  const [viewerIndex, setViewerIndex] = useState<number | null>(null);
  const [limit, setLimit] = useState(SEARCH_LIMIT);

  // Parsing is pure and cheap, but referential stability matters: the filters
  // object is part of the query key.
  const filters = useMemo(() => parseFilters(rawSearch), [rawSearch]);

  // A new question resets the viewer and the "show more" depth.
  useEffect(() => {
    setViewerIndex(null);
    setLimit(SEARCH_LIMIT);
  }, [q, albumId, similar, filters]);

  const setFilters = (next: SearchFilters) => {
    void navigate({
      to: '/search',
      search: { q, albumId, similar, ...filtersToUrl(next) },
    });
  };

  const bySimilarity = similar !== undefined;
  const textResults = useSearchResults(bySimilarity ? '' : q, albumId, filters, limit);
  const similarResults = useSimilarResults(similar, limit);
  const results = bySimilarity ? similarResults : textResults;

  const assets: TimelineAsset[] = results.data?.assets ?? [];
  const searchable = bySimilarity || hasSearchableInput(q, albumId, filters);
  const canShowMore = assets.length >= limit && limit < SEARCH_LIMIT_MAX;

  return (
    <div className="flex h-full flex-col">
      {!bySimilarity && <FilterBar filters={filters} onChange={setFilters} />}

      {albumId && !bySimilarity && <AlbumScopeChip albumId={albumId} q={q} filters={filters} />}
      {bySimilarity && <SimilarChip assetId={similar} />}

      {!searchable ? (
        <EmptyState
          title="Search your library"
          body={
            filters.metadataOnly
              ? 'Type part of a file name, or set a filter - "all videos from 2016" needs no text at all.'
              : 'Describe what you are looking for - "ink sketch of a horse", "red dress on a beach", "foggy street at night". Results are ranked by what is in the picture, not its name.'
          }
        />
      ) : results.isPending ? (
        <PendingState />
      ) : results.isError ? (
        isModelNotReady(results.error) ? (
          <ModelNotReady onReady={() => void results.refetch()} />
        ) : (
          <EmptyState title="Search failed" body={apiErrorMessage(results.error)} />
        )
      ) : assets.length === 0 ? (
        <EmptyState
          title={q ? `Nothing found for "${q}"` : 'Nothing matches'}
          body="Try describing the scene differently, or loosen a filter."
        />
      ) : (
        <>
          <div className="border-b border-line-soft px-4 py-2.5">
            <p className="text-sm text-ink-muted">
              <span className="tabular-nums text-ink">{assets.length.toLocaleString()}</span>{' '}
              {bySimilarity ? (
                assets.length === 1 ? 'similar picture' : 'similar pictures'
              ) : (
                <>
                  {assets.length === 1 ? 'result' : 'results'}
                  {q ? <> for <span className="text-ink">"{q}"</span></> : ' for these filters'}
                </>
              )}
              {!filters.metadataOnly && results.data?.model && (
                <span className="text-ink-faint"> · ranked by {results.data.model}</span>
              )}
              {!filters.metadataOnly && !bySimilarity && !albumId && countActiveFilters(filters) === 0 && (
                <span className="text-ink-faint"> · pictures only</span>
              )}
            </p>
          </div>

          <div className="scrollbar-subtle min-h-0 flex-1 overflow-y-auto p-4">
            <div className="grid grid-cols-2 gap-1.5 sm:grid-cols-4 lg:grid-cols-6 xl:grid-cols-8">
              {assets.map((asset, index) => (
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

            {canShowMore && (
              <div className="flex justify-center py-6">
                <button
                  type="button"
                  onClick={() => setLimit(SEARCH_LIMIT_MAX)}
                  disabled={results.isFetching}
                  className="rounded-md border border-line bg-surface px-4 py-2 text-sm font-medium text-ink-muted transition-colors hover:bg-raised hover:text-ink disabled:opacity-60"
                >
                  {results.isFetching ? 'Loading…' : `Show up to ${SEARCH_LIMIT_MAX}`}
                </button>
              </div>
            )}
          </div>
        </>
      )}

      {viewerIndex !== null && (
        <AssetViewer
          assets={assets}
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
 * the chip both says so and offers the way out.
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
            void navigate({
              to: '/search',
              search: { q, albumId: undefined, similar: undefined, ...filtersToUrl(filters) },
            })
          }
          className="rounded p-0.5 hover:bg-accent hover:text-ink-inverse"
        >
          <CloseGlyph />
        </button>
      </span>
    </div>
  );
}

/** "Pictures like this one", with the one it started from. */
function SimilarChip({ assetId }: { assetId: string | undefined }) {
  const navigate = useNavigate();
  if (!assetId) return null;

  return (
    <div className="flex items-center gap-3 border-b border-line-soft px-4 py-2">
      <img
        src={assetThumbnailUrl(assetId)}
        alt=""
        className="h-10 w-10 rounded-md object-cover ring-1 ring-line"
      />
      <span className="inline-flex items-center gap-1.5 rounded-lg border border-accent bg-accent-muted px-2.5 py-1 text-[13px] font-medium text-accent">
        More like this picture
        <button
          type="button"
          aria-label="Stop searching by this picture"
          onClick={() => void navigate({ to: '/search', search: { q: '', albumId: undefined, similar: undefined } })}
          className="rounded p-0.5 hover:bg-accent hover:text-ink-inverse"
        >
          <CloseGlyph />
        </button>
      </span>
    </div>
  );
}

/**
 * The core answered 503: the model is not loaded. Show where it is - being
 * downloaded, not downloaded, turned off - and refetch the search the moment
 * it reports ready.
 */
function ModelNotReady({ onReady }: { onReady: () => void }) {
  return (
    <div className="grid min-h-0 flex-1 place-items-center px-6">
      <div className="w-full max-w-md rounded-lg border border-line bg-surface p-5">
        <h2 className="text-base font-semibold text-ink">Search is not ready yet</h2>
        <p className="mt-1 text-sm text-ink-muted">
          Searching by meaning needs the model on this machine. Searching by file name works
          now: switch the mode in the bar above.
        </p>
        <div className="mt-4">
          <SearchModelProgress onReady={onReady} />
        </div>
      </div>
    </div>
  );
}

function PendingState() {
  return (
    <div className="min-h-0 flex-1">
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

function CloseGlyph() {
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" aria-hidden>
      <path d="M6 6l12 12M18 6L6 18" />
    </svg>
  );
}
