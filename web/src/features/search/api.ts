import { useInfiniteQuery } from '@tanstack/react-query';
import { getAssetInfo } from '@immich/sdk';
import type { AssetResponseDto, SearchResponseDto } from '@immich/sdk';
import { apiJson } from '@/shared/api/tessera';
import { buildSearchParams, hasSearchableInput, type SearchFilters } from './searchParams';

/**
 * Search queries.
 *
 * These hooks would naturally live in shared/api/queries.ts next to the others,
 * but that file is shared surface owned elsewhere — so the search feature keeps
 * its server-state hooks here, following the same patterns (key factory,
 * TanStack Query owns the cache, no component ever calls the SDK directly).
 */

/**
 * Key factory, scoped to search. Mirrors the `qk` pattern from
 * shared/api/queries.ts: one definition of what a search key looks like, so
 * any future invalidation provably matches.
 *
 * The whole filter object is part of the key — two searches that differ only
 * by a filter are DIFFERENT queries and must not share a cache entry.
 */
export const searchKeys = {
  all: ['search'] as const,
  results: (q: string, albumId: string | undefined, filters: SearchFilters) =>
    [...searchKeys.all, 'results', { q, albumId, filters }] as const,
};

/**
 * Results per page. Large enough that one page fills a desktop viewport with
 * room to scroll, small enough that a cold-start query is not also a huge
 * payload. The server caps `size` at 1000; this is nowhere near it.
 */
export const SEARCH_PAGE_SIZE = 100;

/**
 * Hard ceiling on a single search request.
 *
 * The ML text model unloads after five idle minutes, and the first query after
 * that reloads it — 30 to 60 seconds, during which the request simply hangs.
 * Without a timeout, a genuinely stuck request hangs FOREVER, and the user
 * cannot tell "warming up" from "dead". 120s is double the worst observed
 * warm-up: long enough to never kill a legitimate cold start, short enough
 * that a dead backend fails the same minute rather than never.
 */
const SEARCH_TIMEOUT_MS = 120_000;

/** How long a pending search waits before the UI explains itself. */
export const COLD_START_HINT_MS = 8_000;

export const COLD_START_HINT =
  'Warming up the search model — the first search after a quiet spell takes up to a minute.';

/**
 * SEMANTIC SEARCH GOES THROUGH TESSERA.
 *
 * Tessera asks Foundation - the one store of SigLIP2 vectors on this machine -
 * and answers in the ids this app renders, scoped to the signed-in account.
 * This app never calls Foundation itself: under the house pattern every
 * service is reached through Tessera, which is the only thing that knows
 * what the caller may see and how to spell what it holds.
 *
 * Scope:
 *   - AN ALBUM is sent as its id; Tessera hands Foundation the album's
 *     members by hash and gets an exact ranking of that set back, so an album
 *     whose best match ranks poorly library-wide still returns its best match.
 *   - ANY OTHER FILTER (person, date, camera, favourite) is a metadata
 *     concept. Metadata search answers "which assets satisfy this" with no
 *     embeddings, Tessera answers "in what order", and the two are intersected
 *     here by id.
 *
 * Videos do not appear: Foundation registers images only.
 */

interface TesseraSearchPage {
  page: number;
  size: number;
  count: number;
  hasMore: boolean;
  results: { assetId: string; score: number }[];
}

async function tesseraSearch(
  q: string,
  albumId: string | undefined,
  filters: SearchFilters,
  page: number,
  size: number,
  signal: AbortSignal,
): Promise<TesseraSearchPage> {
  return apiJson<TesseraSearchPage>(`/api/search?${buildSearchParams(q, albumId, filters, page, size)}`, { signal });
}

/** Fetch the assets behind a page of ids, a few at a time. A deleted asset 404s without failing the page. */
async function assetsFor(ids: string[], signal: AbortSignal): Promise<AssetResponseDto[]> {
  const items: AssetResponseDto[] = [];
  for (let i = 0; i < ids.length; i += 8) {
    const batch = await Promise.all(
      ids.slice(i, i + 8).map((id) => getAssetInfo({ id }, { signal }).catch(() => null)),
    );
    for (const asset of batch) if (asset) items.push(asset);
  }
  return items;
}

function pageResponse(items: AssetResponseDto[], total: number, hasMore: boolean, page: number): SearchResponseDto {
  return {
    albums: { items: [], count: 0, total: 0, facets: [] },
    assets: { items, count: items.length, total, facets: [], nextPage: hasMore ? String(page + 1) : null },
  };
}

/** One page: Tessera ranks (or lists), filters and pages; the assets behind the ids come after. */
async function fetchRankedPage(
  q: string,
  albumId: string | undefined,
  filters: SearchFilters,
  page: number,
  size: number,
  signal: AbortSignal,
): Promise<SearchResponseDto> {
  const ranked = await tesseraSearch(q, albumId, filters, page, size, signal);
  const items = await assetsFor(ranked.results.map((r) => r.assetId), signal);
  return pageResponse(items, (page - 1) * size + ranked.count + (ranked.hasMore ? 1 : 0), ranked.hasMore, page);
}

/**
 * One page of a search, with a hard ceiling on how long it may take.
 *
 * TanStack Query hands us a signal that fires when the query is superseded
 * (the user typed a new search) or unmounted. We need to abort on EITHER that
 * or the timeout, and `AbortSignal.any` is newer than this project's ES2022
 * lib - so a small controller bridges the two by hand.
 */
async function fetchSearchPage(
  q: string,
  albumId: string | undefined,
  filters: SearchFilters,
  page: number,
  outerSignal: AbortSignal,
): Promise<SearchResponseDto> {
  const controller = new AbortController();
  const timer = setTimeout(
    () => controller.abort(new DOMException('Search timed out', 'TimeoutError')),
    SEARCH_TIMEOUT_MS,
  );
  const onOuterAbort = () => controller.abort(outerSignal.reason as Error | undefined);
  outerSignal.addEventListener('abort', onOuterAbort);

  try {
    return await fetchRankedPage(q, albumId, filters, page, SEARCH_PAGE_SIZE, controller.signal);
  } catch (error) {
    // Translate our own timeout into a message worth showing. Any other abort
    // (new search, unmount) is TanStack Query's business, and real HTTP errors
    // already carry a status and title from Tessera.
    if (error instanceof DOMException && error.name === 'TimeoutError') {
      throw new Error('The search took too long. If the search model was asleep, try again in a moment.');
    }
    throw error;
  } finally {
    clearTimeout(timer);
    outerSignal.removeEventListener('abort', onOuterAbort);
  }
}

export function useSearchResults(q: string, albumId: string | undefined, filters: SearchFilters) {
  return useInfiniteQuery({
    queryKey: searchKeys.results(q, albumId, filters),
    queryFn: ({ pageParam, signal }) => fetchSearchPage(q, albumId, filters, pageParam, signal),
    initialPageParam: 1,
    getNextPageParam: (last) => {
      const next = last.assets.nextPage;
      return next ? Number(next) : undefined;
    },
    enabled: hasSearchableInput(q, albumId, filters),
    staleTime: 5 * 60_000,
    retry: false,
  });
}
