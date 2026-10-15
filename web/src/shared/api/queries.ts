import { useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { getAllAlbums, getMyUser } from '@immich/sdk';
import type { AlbumResponseDto, UserAdminResponseDto } from '@immich/sdk';
import { getTimeBucket, getTimeBuckets, type TimeBucket, type TimeBucketQuery, type TimelineAsset } from './timeline';

/**
 * Server state lives here, and only here.
 *
 * There is no Redux, Zustand or Context holding API data. Almost everything a
 * photo library displays IS server state — assets, albums — and
 * TanStack Query already solves caching, deduplication, background refetching
 * and request cancellation for it. Copying that data into a second store means
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
 * them here means `queryClient.invalidateQueries({ queryKey: qk.albums.all })`
 * provably matches every album query, because there is one definition of what
 * an album key looks like.
 */
export const qk = {
  me: ['me'] as const,

  albums: {
    all: ['albums'] as const,
    list: () => [...qk.albums.all, 'list'] as const,
    detail: (id: string) => [...qk.albums.all, 'detail', id] as const,
  },


  timeline: {
    all: ['timeline'] as const,
    buckets: (query: TimeBucketQuery) => [...qk.timeline.all, 'buckets', query] as const,
    bucket: (bucket: string, query: TimeBucketQuery) =>
      [...qk.timeline.all, 'bucket', bucket, query] as const,
  },
} as const;

export function useMe(): UseQueryResult<UserAdminResponseDto> {
  return useQuery({
    queryKey: qk.me,
    queryFn: () => getMyUser(),
    // Identity rarely changes mid-session; refetching it is pure noise.
    staleTime: Infinity,
    retry: false,
  });
}

/**
 * The album list.
 *
 * This used to carry a localStorage stale-while-revalidate cache, because
 * `GET /api/albums` took 7.6 SECONDS: Immich recomputed every album's
 * assetCount, startDate and endDate on each request by aggregating 985,003
 * asset rows. The page had nothing to click for seven seconds on every visit.
 *
 * That cache is GONE, because the cause was fixed rather than hidden. The
 * server now reads a denormalised `album_summary` table maintained by a
 * statement-level trigger and a watermark poller (see
 * scripts/sql/album-summary.sql in the deployment repo), and the same endpoint
 * answers in ~57ms.
 *
 * Deleting it mattered for correctness, not just tidiness: a client-side cache
 * of server state is a second copy of the truth, and every mutation had to
 * remember to invalidate it. TanStack Query's own cache is enough now.
 */
export function useAlbums() {
  return useQuery<AlbumResponseDto[]>({
    queryKey: qk.albums.list(),
    queryFn: () => getAllAlbums({}),
    // Five minutes: the folder-to-album creators run every five minutes and
    // most runs change nothing, so this matches the rate the data can actually
    // change. Cheap to refetch now that it is not a 7.6s request.
    staleTime: 5 * 60_000,
  });
}

/**
 * A single album's metadata.
 *
 * Derived from the cached album LIST rather than fetched individually, because
 * the list is already loaded for the albums page — so this costs nothing and
 * saves a request per album visit.
 *
 * v3's `AlbumResponseDto` carries `assetCount` and no assets array, so an
 * individual fetch would add nothing the list does not already hold. When the
 * asset IDS are genuinely needed — merging albums, for instance — the timeline
 * endpoints are the way to enumerate them; see `features/albums/merge.ts`.
 */
export function useAlbum(id: string | undefined) {
  const albums = useAlbums();
  return {
    ...albums,
    data: id ? albums.data?.find((album) => album.id === id) : undefined,
  };
}

/**
 * The bucket index for a timeline view.
 *
 * One small response describes the entire library's shape, which is what lets
 * the grid compute its full height before fetching a single photo.
 */
export function useTimeBuckets(query: TimeBucketQuery): UseQueryResult<TimeBucket[]> {
  return useQuery({
    queryKey: qk.timeline.buckets(query),
    queryFn: () => getTimeBuckets(query),
    staleTime: 60_000,
  });
}

/**
 * One month of assets.
 *
 * `enabled` is the whole point: the grid mounts a hook per visible bucket and
 * only the visible ones fetch. Scrolling flips buckets on, and TanStack Query
 * keeps recently-viewed ones cached so scrolling back is instant.
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
    // Keep buckets in memory a good while — scrolling up and down the same
    // stretch of timeline is the single most common interaction.
    gcTime: 15 * 60_000,
  });
}

/**
 * Imperative bucket prefetch, for scroll-ahead.
 *
 * Called as a bucket approaches the viewport so its images are already in
 * flight by the time it is visible.
 */
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
