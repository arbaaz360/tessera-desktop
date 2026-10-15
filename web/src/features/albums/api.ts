import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiJson } from '@/shared/api/tessera';
import type { AlbumDto } from '@/shared/api/types';
import { qk } from '@/shared/api/queries';

/**
 * Album mutations, in the request shapes the core keeps from the fork
 * (contract §3): `POST /api/albums`, `PATCH /api/albums/{id}`,
 * `DELETE /api/albums/{id}`.
 *
 * The reads live in `shared/api/queries.ts` because several features consume
 * them; these writes are used by the albums feature alone, so they live here.
 * Every mutation invalidates `qk.albums.all`, the PREFIX of all album
 * queries: names, counts and covers surface in several places, and
 * invalidating the family cannot silently miss a new album query added later.
 */

export function useCreateAlbum() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: (albumName: string) =>
      apiJson<AlbumDto>('/api/albums', { method: 'POST', body: JSON.stringify({ albumName }) }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.albums.all }),
  });
}

/**
 * Rename, with an optimistic update.
 *
 *   onMutate  - cancel in-flight list fetches, snapshot the cache, write the
 *               new name into it, and return the snapshot.
 *   onError   - put the snapshot back, so the UI reverts to the truth instead
 *               of lying about a rename the core rejected.
 *   onSettled - invalidate either way, so whatever really happened is refetched.
 *
 * Optimistic here and not for create/delete because a rename edits text the
 * user is LOOKING AT: a title that snaps back for a second after Enter reads
 * as a failed save.
 */
export function useRenameAlbum() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: ({ id, albumName }: { id: string; albumName: string }) =>
      apiJson<AlbumDto>(`/api/albums/${encodeURIComponent(id)}`, {
        method: 'PATCH',
        body: JSON.stringify({ albumName }),
      }),

    onMutate: async ({ id, albumName }) => {
      await client.cancelQueries({ queryKey: qk.albums.list() });

      const previous = client.getQueryData<AlbumDto[]>(qk.albums.list());
      client.setQueryData<AlbumDto[]>(qk.albums.list(), (albums) =>
        albums?.map((album) => (album.id === id ? { ...album, albumName } : album)),
      );

      return { previous };
    },

    onError: (_error, _variables, context) => {
      if (context?.previous) client.setQueryData(qk.albums.list(), context.previous);
    },

    onSettled: () => client.invalidateQueries({ queryKey: qk.albums.all }),
  });
}

/**
 * Choose the album's cover from its own pictures.
 *
 * Sent as `albumThumbnailAssetId` on the same PATCH the rename uses - the
 * field Immich's UpdateAlbumDto always had, on a route the contract keeps.
 * The fork's PATCH only read `albumName` and `description`; the core needs to
 * honour this one field for the choice to persist (noted for the contract).
 */
export function useSetAlbumCover() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: ({ id, assetId }: { id: string; assetId: string }) =>
      apiJson<AlbumDto>(`/api/albums/${encodeURIComponent(id)}`, {
        method: 'PATCH',
        body: JSON.stringify({ albumThumbnailAssetId: assetId }),
      }),
    onMutate: async ({ id, assetId }) => {
      await client.cancelQueries({ queryKey: qk.albums.list() });
      const previous = client.getQueryData<AlbumDto[]>(qk.albums.list());
      client.setQueryData<AlbumDto[]>(qk.albums.list(), (albums) =>
        albums?.map((album) => (album.id === id ? { ...album, albumThumbnailAssetId: assetId } : album)),
      );
      return { previous };
    },
    onError: (_error, _variables, context) => {
      if (context?.previous) client.setQueryData(qk.albums.list(), context.previous);
    },
    onSettled: () => client.invalidateQueries({ queryKey: qk.albums.all }),
  });
}

export function useDeleteAlbum() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: (id: string) =>
      apiJson<void>(`/api/albums/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.albums.all }),
  });
}
