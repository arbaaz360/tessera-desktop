import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { searchUsers, type UserResponseDto } from '@immich/sdk';
import { apiJson } from '@/shared/api/tessera';
import { useMe } from '@/shared/api/queries';

/**
 * Which of my albums also exist, by name, in another personal account.
 *
 * This library is split across accounts filled from different sources, and
 * the sources name their folders the same way — so one name routinely means
 * two half-albums. Seeing that from the grid is the whole point: it is the
 * difference between "this is my 2024 album" and "this is HALF of my 2024".
 *
 * Tessera answers it directly (`GET /api/albums/overlap`): it holds every
 * account's albums in one table, which is the join Immich could never do
 * with a per-user key and the reason a sidecar once needed a database
 * connection to every account. Names compare case-insensitively, the same
 * rule every album lookup uses. Corpus accounts are excluded — a subreddit
 * sharing a name with a folder is a coincidence, not a split.
 */

export interface OverlapSide {
  albumId: string;
  ownerId: string;
  images: number;
  videos: number;
}

/** One album's overlap, oriented from the album being displayed. */
export interface OverlapEntry {
  albumName: string;
  /** The album you are looking at. */
  mine: OverlapSide;
  /** Its same-named counterpart in another personal account. */
  peer: OverlapSide;
}

interface OverlapRow {
  name: string;
  mine: { albumId: string; images: number; videos: number };
  other: { ownerId: string; albumId: string; images: number; videos: number };
}

export const overlapKeys = {
  all: ['albums', 'overlap'] as const,
};

/**
 * The overlap map, keyed by MY album id.
 *
 * Measured at 3.3 s for 1.6M memberships — long enough that badges paint
 * after the grid rather than with it, short enough that TanStack Query's own
 * cache (five minutes, the folder-to-album cadence) is all the caching it
 * needs. Returns an empty map, never undefined: to a badge, "no overlap" and
 * "not loaded yet" are the same instruction — draw nothing.
 */
export function useAlbumOverlap(): Map<string, OverlapEntry> {
  const { data: me } = useMe();

  const query = useQuery<OverlapRow[]>({
    queryKey: overlapKeys.all,
    queryFn: () => apiJson<OverlapRow[]>('/api/albums/overlap'),
    enabled: Boolean(me),
    staleTime: 5 * 60_000,
    retry: false,
  });

  return useMemo(() => {
    const map = new Map<string, OverlapEntry>();
    if (!me) return map;
    for (const row of query.data ?? []) {
      // An album may overlap with more than one other account; the first row
      // (largest combined size, as the server orders them) is the badge.
      if (map.has(row.mine.albumId)) continue;
      map.set(row.mine.albumId, {
        albumName: row.name,
        mine: { albumId: row.mine.albumId, ownerId: me.id, images: row.mine.images, videos: row.mine.videos },
        peer: { ...row.other },
      });
    }
    return map;
  }, [query.data, me]);
}

/** Tessera adds `kind` to the Immich user shape: personal | corpus. */
export type AccountDto = UserResponseDto & { kind?: 'personal' | 'corpus' };

/**
 * Every account, so a tooltip can say "also in Indian" rather than quoting an
 * id, and so the move dialog can offer destinations. `searchUsers` is the
 * non-admin user list. Failure is survivable: without it labels fall back to
 * "the other account", still true, just less specific.
 */
export function useAccounts() {
  return useQuery<AccountDto[]>({
    queryKey: ['users', 'all'],
    queryFn: () => searchUsers() as Promise<AccountDto[]>,
    staleTime: Infinity,
    retry: false,
  });
}

/** Resolve an account id to its display name. */
export function useAccountName(): (userId: string) => string {
  const { data } = useAccounts();
  const names = useMemo(() => {
    const m = new Map<string, string>();
    for (const user of data ?? []) m.set(user.id, user.name);
    return m;
  }, [data]);
  return (userId: string) => names.get(userId) ?? 'the other account';
}

/** "182,227 images and 848 videos" — the counting half of a tooltip. */
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
