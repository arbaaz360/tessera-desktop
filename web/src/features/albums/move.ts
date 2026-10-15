import { useCallback, useState } from 'react';
import type { AlbumResponseDto } from '@immich/sdk';
import { apiJson } from '@/shared/api/tessera';
import type { OverlapEntry } from './overlap';

/**
 * Moving albums to another personal account.
 *
 * In Tessera an album holds CONTENT, and owning the album is enough to see
 * everything in it — so a move is one row changing hands, and it is instant.
 * Nothing on disk moves. The system this replaces copied every original
 * between account folders, because there, seeing a file meant owning it;
 * that is why the old dialog had a progress bar, a poll loop and a list of
 * files that failed. None of that has anything left to describe.
 *
 * One thing to know before moving: the pictures still appear in the SOURCE
 * account's own timeline, because the files still live under its library
 * root. Moving the files themselves is a relocation job, not an album move.
 *
 * If the destination already has an album of the same name the two MERGE —
 * memberships join it, it keeps its cover, the emptied source album goes.
 */

export interface MovePlan {
  albumId: string;
  albumName: string;
  /** Items in the album being moved. */
  assets: number;
  /** The same-named album in the destination, if one exists — the merge warning's whole point. */
  destAlbumAssets: number;
  willMerge: boolean;
}

export interface MoveResult {
  /** The album that now holds the pictures: the moved one, or the one it merged into. */
  albumId: string;
  merged: boolean;
  moved: number;
}

export interface BatchOutcome {
  albumsDone: number;
  albumsTotal: number;
  merged: number;
  moved: number;
  state: 'running' | 'done' | 'error';
  error: string;
}

/**
 * What a move of this album to that account will do, from what the grid
 * already knows: the overlap map says whether a same-named album exists over
 * there and how big it is. No round trip.
 */
export function planMove(
  album: AlbumResponseDto,
  destOwnerId: string,
  overlap: OverlapEntry | null,
): MovePlan {
  const peer = overlap && overlap.peer.ownerId === destOwnerId ? overlap.peer : null;
  return {
    albumId: album.id,
    albumName: album.albumName,
    assets: album.assetCount,
    destAlbumAssets: peer ? peer.images + peer.videos : 0,
    willMerge: peer !== null,
  };
}

export function moveAlbum(albumId: string, ownerId: string): Promise<MoveResult> {
  return apiJson<MoveResult>(`/api/albums/${encodeURIComponent(albumId)}/move`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ownerId }),
  });
}

/**
 * Move one or more albums, one after another.
 *
 * Sequential so a failure is attributable — you know exactly which album was
 * in flight — and because each is a single fast write; parallelism would buy
 * nothing. Progress is local state, not query cache: it belongs to one open
 * dialog and means nothing the moment it closes.
 */
export function useAlbumMove() {
  const [outcome, setOutcome] = useState<BatchOutcome | null>(null);
  const [busy, setBusy] = useState(false);

  const begin = useCallback(async (albums: { id: string; albumName: string }[], destOwnerId: string) => {
    setBusy(true);
    const tally: BatchOutcome = {
      albumsDone: 0,
      albumsTotal: albums.length,
      merged: 0,
      moved: 0,
      state: 'running',
      error: '',
    };
    setOutcome({ ...tally });

    try {
      for (const album of albums) {
        const result = await moveAlbum(album.id, destOwnerId);
        tally.albumsDone += 1;
        tally.moved += result.moved;
        if (result.merged) tally.merged += 1;
        setOutcome({ ...tally });
      }
      setOutcome({ ...tally, state: 'done' });
    } catch (cause) {
      setOutcome({ ...tally, state: 'error', error: (cause as Error).message });
    } finally {
      setBusy(false);
    }
  }, []);

  return { outcome, busy, begin };
}

/**
 * The sentence shown before anything moves. Leads with the merge count,
 * because that is the part that surprises: four albums where three land in
 * existing albums is a different act from four into empty space.
 */
export function describeBatchMove(plans: MovePlan[], destName: string): string {
  const albums = plans.length;
  const assets = plans.reduce((sum, plan) => sum + plan.assets, 0);
  const merging = plans.filter((plan) => plan.willMerge);

  const head =
    `${assets.toLocaleString()} item${assets === 1 ? '' : 's'} across ` +
    `${albums.toLocaleString()} album${albums === 1 ? '' : 's'} will move to ${destName}.`;

  if (merging.length === 0) {
    return `${head} Each keeps its name. Nothing on disk moves and nothing is deleted.`;
  }

  const which =
    merging.length === albums
      ? `All ${albums === 1 ? 'of it' : 'of them'} will MERGE`
      : `${merging.length} of them will MERGE`;

  return `${head} ${which} into an album ${destName} already has of the same name. Nothing on disk moves and nothing is deleted.`;
}
