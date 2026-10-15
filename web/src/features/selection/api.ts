import { useMutation, useQueryClient } from '@tanstack/react-query';
import { addAssetsToAlbum, deleteAssets, downloadArchive, updateAssets } from '@immich/sdk';
import { qk } from '@/shared/api/queries';

/**
 * Server state for bulk actions.
 *
 * Feature-local for the same reason `features/admin/api.ts` is: nothing
 * outside the selection flow performs bulk mutations, so putting these in
 * `shared/api/queries.ts` would misrepresent them as shared. (The viewer also
 * imports a couple of these for its single-asset actions — a bulk endpoint
 * with one id is still the same endpoint.)
 *
 * Every mutation invalidates `qk.timeline.all` rather than surgically patching
 * cached buckets. Patching would be faster on paper, but a favorite flag or a
 * trashed asset can appear in MANY cached queries (the main timeline, a
 * person's grid, an album, the trash view — all keyed differently), and
 * finding every copy by hand is exactly the cache-coherence bug TanStack
 * Query exists to prevent. Invalidation refetches only the buckets actually
 * on screen; the rest just get marked stale.
 */

/** Above this, the download confirm dialog warns before archiving. */
export const DOWNLOAD_WARN_THRESHOLD = 500;

export function useBulkFavorite() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ ids, isFavorite }: { ids: string[]; isFavorite: boolean }) =>
      updateAssets({ assetBulkUpdateDto: { ids, isFavorite } }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.timeline.all }),
  });
}

export function useBulkAddToAlbum() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ albumId, ids }: { albumId: string; ids: string[] }) =>
      addAssetsToAlbum({ id: albumId, bulkIdsDto: { ids } }),
    // Album asset counts (and possibly covers) just changed; the cached album
    // list renders both.
    onSuccess: () => client.invalidateQueries({ queryKey: qk.albums.all }),
  });
}

/**
 * Delete-to-trash. `force: false` is the entire contract: the server marks
 * the assets trashed instead of unlinking files, and the Trash page can
 * restore them. Nothing in this app ever calls the force path — permanent
 * deletion happens only through "Empty trash", behind its own typed
 * confirmation.
 */
export function useBulkTrash() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (ids: string[]) => deleteAssets({ assetBulkDeleteDto: { ids, force: false } }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.timeline.all }),
  });
}

/**
 * Download selected assets as a zip.
 *
 * The SDK's `downloadArchive` POSTs the id list and resolves to a `Blob`, so
 * "saving" it means the classic object-URL dance: mint a URL for the blob,
 * click a synthetic anchor at it, revoke the URL. The revoke matters — an
 * object URL pins its blob in memory for the lifetime of the page, and these
 * archives can run to gigabytes.
 *
 * Wrapped in a mutation purely for the free `isPending` — archiving hundreds
 * of originals server-side takes real time, and the button needs to say so.
 */
export function useBulkDownload() {
  return useMutation({
    mutationFn: async (ids: string[]) => {
      const blob = await downloadArchive({ downloadArchiveDto: { assetIds: ids } });

      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = `tessera-${new Date().toISOString().slice(0, 10)}.zip`;
      anchor.click();
      URL.revokeObjectURL(url);
    },
  });
}
