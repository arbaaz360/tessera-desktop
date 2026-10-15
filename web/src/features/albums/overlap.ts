import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { apiJson } from '@/shared/api/tessera';
import type { OverlapRow } from '@/shared/api/types';
import { qk } from '@/shared/api/queries';

/**
 * Which of my albums share a name with another album.
 *
 * Folders are albums here, and two folders in two roots named the same way -
 * "2024", "sketches" - give two half-albums under one name. Seeing that from
 * the grid is the point: it is the difference between "this is my 2024 album"
 * and "this is HALF of my 2024". The core answers it directly
 * (`GET /api/albums/overlap`, contract §3); names compare case-insensitively.
 */

export interface OverlapSide {
  albumId: string;
  images: number;
  videos: number;
}

/** One album's overlap, oriented from the album being displayed. */
export interface OverlapEntry {
  albumName: string;
  /** The album you are looking at. */
  mine: OverlapSide;
  /** Its same-named counterpart. */
  peer: OverlapSide;
}

/**
 * The overlap map, keyed by MY album id. Returns an empty map, never
 * undefined: to a badge, "no overlap" and "not loaded yet" are the same
 * instruction - draw nothing.
 */
export function useAlbumOverlap(): Map<string, OverlapEntry> {
  const query = useQuery<OverlapRow[]>({
    queryKey: qk.albums.overlap(),
    queryFn: () => apiJson<OverlapRow[]>('/api/albums/overlap'),
    staleTime: 5 * 60_000,
    retry: false,
  });

  return useMemo(() => {
    const map = new Map<string, OverlapEntry>();
    for (const row of query.data ?? []) {
      // An album may overlap with more than one other; the first row (largest
      // combined size, as the core orders them) is the badge.
      if (map.has(row.mine.albumId)) continue;
      map.set(row.mine.albumId, {
        albumName: row.name,
        mine: { albumId: row.mine.albumId, images: row.mine.images, videos: row.mine.videos },
        peer: { albumId: row.other.albumId, images: row.other.images, videos: row.other.videos },
      });
    }
    return map;
  }, [query.data]);
}

/** "182 images and 8 videos" - the counting half of a tooltip. */
export function describeCounts(side: OverlapSide): string {
  const parts: string[] = [];
  if (side.images > 0) {
    parts.push(`${side.images.toLocaleString()} image${side.images === 1 ? '' : 's'}`);
  }
  if (side.videos > 0) {
    parts.push(`${side.videos.toLocaleString()} video${side.videos === 1 ? '' : 's'}`);
  }
  return parts.length > 0 ? parts.join(' and ') : 'nothing';
}
