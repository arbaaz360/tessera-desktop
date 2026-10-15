import { useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { apiJson } from './tessera';
import type { AlbumDto } from './types';
import { getTimeBucket, getTimeBuckets, type TimeBucket, type TimeBucketQuery, type TimelineAsset } from './timeline';

/**
 * Server state lives here, and only here.
 *
 * There is no Redux, Zustand or Context holding API data. Almost everything a
 * photo library displays IS server state - assets, albums - and TanStack
 * Query already solves caching, deduplication, background refetching and
 * request cancellation for it. Copying that data into a second store means
 * maintaining two versions of the truth and writing the sync code by hand.
 *
 * Client state (which asset is selected, is the sidebar open) is genuinely
 * different and lives in `useState` or the URL, close to where it is used.
 */

/**
 * Query keys as a factory.
 *
 * Keys are how the cache is addressed AND how invalidation is targeted, so
 * scattering string literals through the app makes both unreliable. Building
 * them here means `invalidateQueries({ queryKey: qk.albums.all })` provably
 * matches every album query, because there is one definition of what an
 * album key looks like.
 */
export const qk = {
  albums: {
    all: ['albums'] as const,
    list: () => [...qk.albums.all, 'list'] as const,
    containing: (assetId: string) => [...qk.albums.all, 'containing', assetId] as const,
    overlap: () => [...qk.albums.all, 'overlap'] as const,
  },

  asset: {
    all: ['asset'] as const,
    info: (id: string) => [...qk.asset.all, 'info', id] as const,
  },

  timeline: {
    all: ['timeline'] as const,
    buckets: (query: TimeBucketQuery) => [...qk.timeline.all, 'buckets', query] as const,
    bucket: (bucket: string, query: TimeBucketQuery) =>
      [...qk.timeline.all, 'bucket', bucket, query] as const,
  },

  library: {
    all: ['library'] as const,
    summary: () => [...qk.library.all, 'summary'] as const,
    duplicates: () => [...qk.library.all, 'duplicates'] as const,
  },

  search: {
    all: ['search'] as const,
    status: () => [...qk.search.all, 'status'] as const,
  },

  settings: ['settings'] as const,
  licence: ['licence'] as const,
} as const;

/** The album list. One request, ~ms on SQLite; cheap to refetch. */
export function useAlbums() {
  return useQuery<AlbumDto[]>({
    queryKey: qk.albums.list(),
    queryFn: () => apiJson<AlbumDto[]>('/api/albums'),
    staleTime: 60_000,
  });
}

/**
 * A single album's metadata, derived from the cached album LIST rather than
 * fetched individually: the list is already loaded for the albums page, so
 * this costs nothing and saves a request per album visit.
 */
export function useAlbum(id: string | undefined) {
  const albums = useAlbums();
  return {
    ...albums,
    data: id ? albums.data?.find((album) => album.id === id) : undefined,
  };
}

/**
 * The bucket index for a timeline view. One small response describes the
 * entire library's shape, which is what lets the grid compute its full height
 * before fetching a single photo.
 */
export function useTimeBuckets(query: TimeBucketQuery): UseQueryResult<TimeBucket[]> {
  return useQuery({
    queryKey: qk.timeline.buckets(query),
    queryFn: () => getTimeBuckets(query),
    staleTime: 60_000,
  });
}

/**
 * One month of assets. `enabled` is the whole point: the grid mounts a hook
 * per visible bucket and only the visible ones fetch.
 */
export function useTimeBucketAssets(
  bucket: string | null,
  query: TimeBucketQuery,
): UseQueryResult<TimelineAsset[]> {
  return useQuery({
    queryKey: qk.timeline.bucket(bucket ?? '', query),
    queryFn: () => getTimeBucket(bucket!, query),
    enabled: Boolean(bucket),
    staleTime: 5 * 60_000,
    // Scrolling up and down the same stretch is the most common interaction.
    gcTime: 15 * 60_000,
  });
}

/** Imperative bucket prefetch, for scroll-ahead. */
export function usePrefetchBucket(query: TimeBucketQuery) {
  const client = useQueryClient();

  return (bucket: string) => {
    void client.prefetchQuery({
      queryKey: qk.timeline.bucket(bucket, query),
      queryFn: () => getTimeBucket(bucket, query),
      staleTime: 5 * 60_000,
    });
  };
}
