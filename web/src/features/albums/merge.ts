import { addAssetsToAlbum, deleteAlbum } from '@immich/sdk';
import type { AlbumResponseDto } from '@immich/sdk';
import { getTimeBucket, getTimeBuckets } from '@/shared/api/timeline';

/**
 * Merging albums.
 *
 * The rule this deployment settled on, after 14 duplicate albums had to be
 * merged by hand: one name, one album. This is the interactive version of that
 * cleanup — pick several albums, choose which survives, and the rest hand over
 * their assets and disappear.
 *
 * Three properties make it safe to run without a preview of every asset:
 *
 * - `(albumId, assetId)` is a PRIMARY KEY, so re-adding an asset the keeper
 *   already holds is a no-op rather than a duplicate row. Overlapping albums
 *   merge cleanly and the count simply lands lower than the naive sum.
 * - Deleting an album NEVER deletes assets. Everything stays in the library
 *   and in any other album it belongs to.
 * - The keeper is only ever added to. Nothing about it is rewritten, so its
 *   cover, its shares and its cover-policy lock all survive untouched.
 *
 * The one ordering rule that matters: assets are copied to the keeper and the
 * copy is VERIFIED before the source album is deleted. Deleting first, or
 * assuming success, is how a merge becomes a data-loss bug.
 */

/**
 * Every asset id in an album, via the timeline endpoints.
 *
 * v3's `AlbumResponseDto` carries `assetCount` and no assets, so there is no
 * single call that returns an album's contents. The timeline is the way in:
 * one small request describes the album's months, and each bucket returns
 * columnar ids. That is far less data than a full DTO per asset — which
 * matters when the largest album here holds 183,075 of them.
 *
 * Buckets are fetched sequentially. A merge is a background operation with a
 * progress line, not something anyone is watching a spinner for, and twenty
 * parallel requests against a library this size help nobody.
 */
async function albumAssetIds(albumId: string): Promise<string[]> {
  const buckets = await getTimeBuckets({ albumId });
  const ids: string[] = [];

  for (const bucket of buckets) {
    const assets = await getTimeBucket(bucket.timeBucket, { albumId });
    for (const asset of assets) ids.push(asset.id);
  }

  return ids;
}

export interface MergeProgress {
  /** Album currently being processed, for the progress line. */
  albumName: string;
  /** How many of the source albums are finished. */
  done: number;
  total: number;
}

export interface MergeOutcome {
  keeperName: string;
  /** Assets that were not already in the keeper. */
  moved: number;
  /** Source albums removed. */
  absorbed: number;
  /** Albums that failed, with the reason; these are NOT deleted. */
  failures: { name: string; message: string }[];
}

/**
 * Merge `sources` into `keeper`.
 *
 * Sequential rather than parallel on purpose: the largest album here holds
 * 183,075 assets, and firing several of those requests at once buys nothing
 * while making the progress report meaningless and the failure state harder
 * to reason about.
 */
export async function mergeAlbums(
  keeper: AlbumResponseDto,
  sources: AlbumResponseDto[],
  onProgress?: (progress: MergeProgress) => void,
): Promise<MergeOutcome> {
  const outcome: MergeOutcome = {
    keeperName: keeper.albumName,
    moved: 0,
    absorbed: 0,
    failures: [],
  };

  for (const [index, source] of sources.entries()) {
    onProgress?.({ albumName: source.albumName, done: index, total: sources.length });

    try {
      const ids = await albumAssetIds(source.id);

      if (ids.length > 0) {
        const results = await addAssetsToAlbum({ id: keeper.id, bulkIdsDto: { ids } });
        const added = results.filter((result) => result.success).length;
        const failed = results.filter(
          (result) => !result.success && result.error !== 'duplicate',
        );

        // A genuine failure means some asset did not make it across. Stop
        // before deleting: an incomplete copy followed by a delete is the one
        // outcome this function must never produce.
        if (failed.length > 0) {
          outcome.failures.push({
            name: source.albumName,
            message: `${failed.length} of ${ids.length} assets could not be copied — album kept`,
          });
          continue;
        }

        outcome.moved += added;
      }

      await deleteAlbum({ id: source.id });
      outcome.absorbed += 1;
    } catch (error) {
      outcome.failures.push({ name: source.albumName, message: (error as Error).message });
    }
  }

  onProgress?.({ albumName: '', done: sources.length, total: sources.length });
  return outcome;
}

/** A one-line summary of what a merge will do, for the confirm dialog. */
export function describeMerge(keeper: AlbumResponseDto, sources: AlbumResponseDto[]): string {
  const incoming = sources.reduce((sum, album) => sum + album.assetCount, 0);
  return (
    `${sources.length} album${sources.length === 1 ? '' : 's'} ` +
    `(${incoming.toLocaleString()} item${incoming === 1 ? '' : 's'}) ` +
    `will be merged into “${keeper.albumName}” and then deleted. ` +
    `Items already in “${keeper.albumName}” are skipped, so the final count may be lower than ` +
    `${(keeper.assetCount + incoming).toLocaleString()}. No photos are deleted.`
  );
}
