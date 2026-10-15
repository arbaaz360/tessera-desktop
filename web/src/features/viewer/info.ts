import { useQuery } from '@tanstack/react-query';
import { apiJson } from '@/shared/api/tessera';
import type { AlbumDto, AssetDto } from '@/shared/api/types';
import { qk } from '@/shared/api/queries';

/**
 * Data for the viewer's info panel.
 *
 * Two sources, because no single one has the whole answer:
 *
 *   - `GET /api/assets/{id}`          the file: name, path, size, dimensions.
 *                                     The timeline endpoints deliberately do
 *                                     not carry these - they are columnar
 *                                     arrays built for scrolling, and a path
 *                                     per tile would bloat every bucket.
 *   - `GET /api/albums?assetId=`      which albums contain this picture.
 *
 * Both are `enabled`-gated so nothing is fetched until the panel is actually
 * open. A closed panel costs zero requests, which matters when paging through
 * pictures with the arrow keys.
 */

export function useAssetInfo(assetId: string | undefined, enabled: boolean) {
  return useQuery<AssetDto>({
    queryKey: qk.asset.info(assetId ?? ''),
    queryFn: () => apiJson<AssetDto>(`/api/assets/${encodeURIComponent(assetId!)}`),
    enabled: enabled && Boolean(assetId),
    // A file's identity is its hash; its metadata changes only on a rescan.
    staleTime: 5 * 60_000,
    retry: false,
  });
}

/**
 * The albums containing this picture. Usually one (its folder), occasionally
 * several - a manual album on top. The panel shows all of them rather than
 * picking a winner.
 */
export function useAssetAlbums(assetId: string | undefined, enabled: boolean) {
  return useQuery<AlbumDto[]>({
    queryKey: qk.albums.containing(assetId ?? ''),
    queryFn: () => apiJson<AlbumDto[]>(`/api/albums?assetId=${encodeURIComponent(assetId!)}`),
    enabled: enabled && Boolean(assetId),
    staleTime: 5 * 60_000,
    retry: false,
  });
}

/** "6000 × 4000 (24.0 MP)" - the megapixel count is the part people compare. */
export function formatDimensions(
  width: number | null | undefined,
  height: number | null | undefined,
): string | null {
  if (!width || !height) return null;
  const megapixels = (width * height) / 1_000_000;
  return `${width.toLocaleString()} × ${height.toLocaleString()} (${megapixels.toFixed(1)} MP)`;
}
