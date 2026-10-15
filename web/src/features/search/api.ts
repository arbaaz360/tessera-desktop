import { useQuery } from '@tanstack/react-query';
import { apiJson } from '@/shared/api/tessera';
import { qk } from '@/shared/api/queries';
import { searchItemToTimelineAsset, type TimelineAsset } from '@/shared/api/timeline';
import type { SearchItem } from '@/shared/api/types';
import { buildSearchParams, hasSearchableInput, type SearchFilters } from './searchParams';

/**
 * Search queries, against the core's local index (contract §4).
 *
 * One request, one ranked list. The core embeds the words with the text
 * tower, scores every picture's vector, filters by album, type and date, and
 * answers `{ items, model }` where each item is a timeline row plus a score.
 * The hits render in the same grid and viewer as everything else.
 *
 * Until the model is loaded the core answers 503 "model not ready". That is
 * not a failure to retry (the query client never retries a 503); the page
 * shows the download or embedding progress instead - see `SearchPage`.
 */

/** The envelope the core returns from `/api/search` and `/api/search/similar/{id}`. */
export interface SearchAnswer {
  items: SearchItem[];
  model: string;
}

export const searchKeys = {
  all: qk.search.all,
  results: (q: string, albumId: string | undefined, filters: SearchFilters, limit: number) =>
    [...qk.search.all, 'results', { q, albumId, filters, limit }] as const,
  similar: (contentId: string, limit: number) => [...qk.search.all, 'similar', contentId, limit] as const,
};

/** Hits per search. Enough to fill a desktop viewport several times over. */
export const SEARCH_LIMIT = 100;
/** The core's ceiling (contract §4). "Show more" goes straight to it. */
export const SEARCH_LIMIT_MAX = 500;

export interface SearchResult {
  assets: TimelineAsset[];
  model: string;
}

function toResult(answer: SearchAnswer): SearchResult {
  return { assets: answer.items.map(searchItemToTimelineAsset), model: answer.model };
}

export function useSearchResults(
  q: string,
  albumId: string | undefined,
  filters: SearchFilters,
  limit: number = SEARCH_LIMIT,
) {
  return useQuery({
    queryKey: searchKeys.results(q, albumId, filters, limit),
    queryFn: ({ signal }) =>
      apiJson<SearchAnswer>(`/api/search?${buildSearchParams(q, albumId, filters, limit)}`, { signal }),
    select: toResult,
    enabled: hasSearchableInput(q, albumId, filters),
    staleTime: 5 * 60_000,
    retry: false,
  });
}

/**
 * "More like this": the picture's own vector as the query. The id is the
 * asset id the grid already holds; the core resolves it to the content.
 */
export function useSimilarResults(contentId: string | undefined, limit: number = SEARCH_LIMIT) {
  return useQuery({
    queryKey: searchKeys.similar(contentId ?? '', limit),
    queryFn: ({ signal }) =>
      apiJson<SearchAnswer>(
        `/api/search/similar/${encodeURIComponent(contentId!)}?limit=${limit}`,
        { signal },
      ),
    select: toResult,
    enabled: Boolean(contentId),
    staleTime: 5 * 60_000,
    retry: false,
  });
}
