import {
  keepPreviousData,
  useMutation,
  useQuery,
  useQueryClient,
  type UseQueryResult,
} from '@tanstack/react-query';
import { apiJson } from '@/shared/api/tessera';

/**
 * Music, through Tessera.
 *
 * Tessera owns the facts - which track plays in which video, and the queue
 * that finds out (docs/contracts/music.md). A worker does the listening; this
 * app never talks to it. Same origin, same session, the ids this app already
 * renders. The sidecar on :2298 that kept its ledger inside Immich's database
 * is no longer in this path.
 */

/** A video's music state, derived by Tessera: never asked, waiting, being asked, answered, or given up. */
export type MusicState = 'never' | 'pending' | 'running' | 'matched' | 'no_match' | 'failed';

/** GET /api/music/status - the caller's videos, counted. */
export interface MusicStatus {
  videos: number;
  pending: number;
  processing: number;
  matched: number;
  noMatch: number;
  failed: number;
  /** Never asked - the number the old service could not tell you. */
  never: number;
  progressPercent: number;
  paused: boolean;
  lastIdentifiedAt: string | null;
  identifierAvailable: boolean;
}

export interface TrackSummary {
  key: string;
  title: string;
  artist: string;
  album: string | null;
  artworkUrl: string | null;
}

/** GET /api/assets/{id}/music */
export interface AssetMusic {
  assetId: string;
  state: MusicState;
  attempts: number;
  error: string | null;
  identifiedAt: string | null;
  track: TrackSummary | null;
  /** Videos in the library sharing this track (0 when none). */
  sameTrackCount: number;
}

/** Tessera omits null fields from its JSON, so `takenAt` and `durationMs` may be absent. */
export interface TrackVideo {
  id: string;
  fileName: string;
  takenAt?: string | null;
  durationMs?: number | null;
}

/** GET /api/music/tracks/{key}/assets - the track plus one page of the caller's videos. */
export interface TrackVideosPage {
  track: TrackSummary;
  total: number;
  page: number;
  size: number;
  items: TrackVideo[];
}

export type ScanMode = 'missing' | 'all' | 'no_match' | 'failed';

export interface IdentifyUploadResult {
  matched: boolean;
  track: TrackSummary | null;
  sameTrackCount: number;
}

export const TRACK_PAGE_SIZE = 60;

export const musicKeys = {
  all: ['music'] as const,
  status: () => [...musicKeys.all, 'status'] as const,
  asset: (assetId: string) => [...musicKeys.all, 'asset', assetId] as const,
  trackVideos: (key: string, page: number) => [...musicKeys.all, 'track', key, page] as const,
} as const;

/** Polls while the page is open; a queue that is draining should be seen draining. */
export function useMusicStatus(): UseQueryResult<MusicStatus> {
  return useQuery({
    queryKey: musicKeys.status(),
    queryFn: () => apiJson<MusicStatus>('/api/music/status'),
    refetchInterval: 5_000,
    retry: false,
  });
}

export function useAssetMusic(assetId: string): UseQueryResult<AssetMusic> {
  return useQuery({
    queryKey: musicKeys.asset(assetId),
    queryFn: () => apiJson<AssetMusic>(`/api/assets/${encodeURIComponent(assetId)}/music`),
    // Keep asking while it is in the queue, so the answer appears without a reload.
    refetchInterval: (q) =>
      q.state.data?.state === 'pending' || q.state.data?.state === 'running' ? 4_000 : false,
    retry: false,
  });
}

export function useTrackVideos(key: string, page: number): UseQueryResult<TrackVideosPage> {
  return useQuery({
    queryKey: musicKeys.trackVideos(key, page),
    queryFn: () =>
      apiJson<TrackVideosPage>(
        `/api/music/tracks/${encodeURIComponent(key)}/assets?page=${page}&size=${TRACK_PAGE_SIZE}`,
      ),
    placeholderData: keepPreviousData,
    staleTime: 60_000,
  });
}

/** Queue the caller's videos. `missing` is the usual answer; the rest re-ask. */
export function useMusicScan() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (mode: ScanMode) =>
      apiJson<{ queued: number; mode: string }>('/api/music/scan', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ mode }),
      }),
    onSuccess: () => void client.invalidateQueries({ queryKey: musicKeys.status() }),
  });
}

/**
 * Pause or resume the music job. The same control the jobs page has -
 * Tessera's queue, not a switch of the sidecar's own.
 */
export function useMusicToggle() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (action: 'pause' | 'resume') =>
      apiJson<void>('/api/jobs/music', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ command: action }),
      }),
    onSuccess: () => void client.invalidateQueries({ queryKey: musicKeys.status() }),
  });
}

/** POST /api/assets/{id}/identify - ask about one video ahead of everything else. */
export function useIdentifyAsset(assetId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () =>
      apiJson<{ assetId: string; queued: boolean }>(
        `/api/assets/${encodeURIComponent(assetId)}/identify`,
        { method: 'POST' },
      ),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: musicKeys.asset(assetId) });
      void client.invalidateQueries({ queryKey: musicKeys.status() });
    },
  });
}

/** POST /api/music/identify-upload - name the track in a file that is not in the library. */
export function useIdentifyUpload() {
  return useMutation({
    mutationFn: async (file: File): Promise<IdentifyUploadResult> => {
      const form = new FormData();
      form.append('file', file);
      return apiJson<IdentifyUploadResult>('/api/music/identify-upload', {
        method: 'POST',
        body: form,
      });
    },
  });
}
