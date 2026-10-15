import { useQuery } from '@tanstack/react-query';
import { getAllAlbums, getAssetInfo } from '@immich/sdk';
import type { AlbumResponseDto, AssetResponseDto } from '@immich/sdk';
import { apiJson } from '@/shared/api/tessera';

/**
 * Data for the viewer's info panel.
 *
 * Three separate sources, because no single one has the whole answer:
 *
 *   - `getAssetInfo`  the asset's EXIF. The timeline endpoints deliberately
 *                     do not carry it — they are columnar arrays built for
 *                     scrolling 1.6M assets, and camera settings would bloat
 *                     every bucket for something only one photo at a time
 *                     needs.
 *   - `getAllAlbums`  which albums contain this asset. `?assetId=` is a
 *                     documented filter on the album list endpoint, and the
 *                     only way to ask the question from a browser.
 *   - the sidecar     the album's SIZE. Immich has no size field on an album,
 *                     and computing one through the API would mean fetching
 *                     every asset in it — 183,075 rows for the largest album
 *                     here — purely to add up numbers the database can sum.
 *
 * All three are `enabled`-gated so nothing is fetched until the panel is
 * actually open. A closed panel costs zero requests, which matters when paging
 * through photos with the arrow keys.
 */

export function useAssetInfo(assetId: string | undefined, enabled: boolean) {
  return useQuery<AssetResponseDto>({
    queryKey: ['asset', 'info', assetId],
    queryFn: () => getAssetInfo({ id: assetId! }),
    enabled: enabled && Boolean(assetId),
    // EXIF is written once at import and never changes afterwards.
    staleTime: Infinity,
    retry: false,
  });
}

/**
 * The albums containing this asset.
 *
 * Usually one, occasionally several — the folder-to-album creators put an
 * asset in exactly one album, but nothing stops a manual add. The panel shows
 * all of them rather than picking a winner, because "which album is this in"
 * has no single right answer and silently choosing one would be a lie.
 */
export function useAssetAlbums(assetId: string | undefined, enabled: boolean) {
  return useQuery<AlbumResponseDto[]>({
    queryKey: ['albums', 'containing', assetId],
    queryFn: () => getAllAlbums({ assetId: assetId! }),
    enabled: enabled && Boolean(assetId),
    staleTime: 5 * 60_000,
    retry: false,
  });
}

export interface AlbumStats {
  bytes: number;
  total: number;
  images: number;
  videos: number;
}

/**
 * Size and media counts for one album, from Tessera.
 *
 * One aggregate over the album's content - byte sizes are recorded when a
 * file is enrolled, so there is no "still queued for metadata" case here.
 * About 100 ms for the largest album.
 */
export function useAlbumStats(albumId: string | undefined, enabled: boolean) {
  return useQuery<AlbumStats>({
    queryKey: ['albums', 'stats', albumId],
    queryFn: () => apiJson<AlbumStats>(`/api/albums/${encodeURIComponent(albumId!)}/stats`),
    enabled: enabled && Boolean(albumId),
    staleTime: 5 * 60_000,
    retry: false,
  });
}

/**
 * "1/125" — exposure time, as a photographer reads it.
 *
 * Immich stores this as a string already ("1/125", "2", "0.5"), so the only
 * work is turning a bare decimal under a second into the fraction people
 * actually recognise. A value at or above one second stays decimal, with an
 * "s" so it cannot be mistaken for a fraction.
 */
export function formatExposure(value: string | null | undefined): string | null {
  if (!value) return null;
  if (value.includes('/')) return value;

  const seconds = Number(value);
  if (!Number.isFinite(seconds) || seconds <= 0) return value;
  if (seconds >= 1) return `${seconds}s`;
  return `1/${Math.round(1 / seconds)}`;
}

/** "24 mm", "f/1.8", "ISO 400" — units attached, missing values dropped. */
export function formatLens(exif: {
  focalLength?: number | null;
  fNumber?: number | null;
  iso?: number | null;
  exposureTime?: string | null;
}): string | null {
  const parts: string[] = [];
  if (exif.focalLength) parts.push(`${Math.round(exif.focalLength)} mm`);
  if (exif.fNumber) parts.push(`f/${exif.fNumber}`);
  const exposure = formatExposure(exif.exposureTime);
  if (exposure) parts.push(exposure);
  if (exif.iso) parts.push(`ISO ${exif.iso}`);
  return parts.length > 0 ? parts.join(' · ') : null;
}

/** "6000 × 4000 (24.0 MP)" — the megapixel count is the part people compare. */
export function formatDimensions(
  width: number | null | undefined,
  height: number | null | undefined,
): string | null {
  if (!width || !height) return null;
  const megapixels = (width * height) / 1_000_000;
  return `${width.toLocaleString()} × ${height.toLocaleString()} (${megapixels.toFixed(1)} MP)`;
}
