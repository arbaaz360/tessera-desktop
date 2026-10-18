import { useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { apiJson } from '@/shared/api/tessera';
import type { BulkIdResponseDto } from '@/shared/api/types';
import { qk } from '@/shared/api/queries';

/**
 * Server state for bulk actions, in the request shapes the core keeps from
 * the fork (contract §3): favourites and trash on `/api/assets`, membership
 * on `/api/albums/{id}/assets`.
 *
 * Every mutation invalidates `qk.timeline.all` rather than surgically patching
 * cached buckets. A favourite flag or a trashed asset can appear in MANY
 * cached queries (the main timeline, an album, the trash view - all keyed
 * differently), and finding every copy by hand is exactly the cache-coherence
 * bug TanStack Query exists to prevent. Invalidation refetches only the
 * buckets actually on screen; the rest just get marked stale.
 */

export function useBulkFavorite() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ ids, isFavorite }: { ids: string[]; isFavorite: boolean }) =>
      apiJson<void>('/api/assets', { method: 'PUT', body: JSON.stringify({ ids, isFavorite }) }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.timeline.all }),
  });
}

/**
 * What a change of album membership changes: the album list (counts,
 * covers) AND every timeline, because an album's grid is a timeline query
 * keyed by `albumId`. Invalidating only the list once left an album opened
 * while empty showing "This album is empty" under "10 items" for a minute
 * after ten pictures were added (Windows install, 2026-10-18).
 */
export function invalidateAfterAlbumChange(client: QueryClient): Promise<unknown> {
  return Promise.all([
    client.invalidateQueries({ queryKey: qk.albums.all }),
    client.invalidateQueries({ queryKey: qk.timeline.all }),
  ]);
}

export function useBulkAddToAlbum() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ albumId, ids }: { albumId: string; ids: string[] }) =>
      apiJson<BulkIdResponseDto[]>(`/api/albums/${encodeURIComponent(albumId)}/assets`, {
        method: 'PUT',
        body: JSON.stringify({ ids }),
      }),
    onSuccess: () => invalidateAfterAlbumChange(client),
  });
}

/** Take pictures out of an album. The pictures stay in the library. */
export function useBulkRemoveFromAlbum() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ albumId, ids }: { albumId: string; ids: string[] }) =>
      apiJson<BulkIdResponseDto[]>(`/api/albums/${encodeURIComponent(albumId)}/assets`, {
        method: 'DELETE',
        body: JSON.stringify({ ids }),
      }),
    onSuccess: () => invalidateAfterAlbumChange(client),
  });
}

/**
 * What a picture entering or leaving the trash changes: every timeline (the
 * grid, albums, the trash itself) and the library's counts, which the
 * sidebar footer and the Library page show. Without the second the footer
 * kept the old picture count until its next slow poll.
 */
export function invalidateAfterTrashChange(client: QueryClient): Promise<unknown> {
  return Promise.all([
    client.invalidateQueries({ queryKey: qk.timeline.all }),
    client.invalidateQueries({ queryKey: qk.library.all }),
  ]);
}

/**
 * Trash. `force: false` is the entire contract: the core marks the pictures
 * trashed instead of touching files, and the Trash page can restore them.
 * Nothing in this app ever sends `force: true`; the core answers 403 to it.
 */
export function useBulkTrash() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (ids: string[]) =>
      apiJson<void>('/api/assets', { method: 'DELETE', body: JSON.stringify({ ids, force: false }) }),
    onSuccess: () => invalidateAfterTrashChange(client),
  });
}
