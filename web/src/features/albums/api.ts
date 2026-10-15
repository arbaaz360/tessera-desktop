import { useMutation, useQueryClient } from '@tanstack/react-query';
import { createAlbum, deleteAlbum, updateAlbumInfo } from '@immich/sdk';
import type { AlbumResponseDto } from '@immich/sdk';
import { qk } from '@/shared/api/queries';

/**
 * Album mutations.
 *
 * The reads live in `shared/api/queries.ts` because several features consume
 * them; these writes are used by the albums feature alone, so they live here —
 * shared state stays shared, feature logic stays in the feature. They still
 * address the cache through the same `qk` factory, which is what guarantees an
 * invalidation here actually hits the queries defined there.
 *
 * A note on invalidation scope: every mutation invalidates `qk.albums.all`,
 * the PREFIX of all album queries, rather than the specific list key. Covers,
 * counts and names surface in several places; invalidating the family is one
 * line and cannot silently miss a new album query added later. The list is
 * 4,878 albums, so a refetch is not free — but it is one request, and it is
 * the price of never showing a stale name next to a fresh one.
 */

export function useCreateAlbum() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: (albumName: string) => createAlbum({ createAlbumDto: { albumName } }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.albums.all }),
  });
}

/**
 * Rename, with an optimistic update.
 *
 * This is the full TanStack optimistic pattern, worth reading closely because
 * every piece exists for a failure mode:
 *
 *   onMutate  — cancel in-flight list fetches (a response landing mid-edit
 *               would overwrite our optimistic name), snapshot the cache,
 *               write the new name into it, and return the snapshot.
 *   onError   — put the snapshot back. The UI reverts to the truth instead of
 *               lying about a rename the server rejected.
 *   onSettled — invalidate either way, so whatever really happened is refetched.
 *
 * Why optimistic here and not for create/delete? Rename edits text the user is
 * LOOKING AT — a title that snaps back to the old name for a second after
 * pressing Enter reads as a failed save. Create and delete navigate away, so
 * plain invalidation is indistinguishable and much simpler.
 */
export function useRenameAlbum() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: ({ id, albumName }: { id: string; albumName: string }) =>
      updateAlbumInfo({ id, updateAlbumDto: { albumName } }),

    onMutate: async ({ id, albumName }) => {
      await client.cancelQueries({ queryKey: qk.albums.list() });

      const previous = client.getQueryData<AlbumResponseDto[]>(qk.albums.list());
      client.setQueryData<AlbumResponseDto[]>(qk.albums.list(), (albums) =>
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

export function useDeleteAlbum() {
  const client = useQueryClient();

  return useMutation({
    // Wrapped in an async block because the SDK types this endpoint's 204
    // response as `Promise<never>`; awaiting it and returning void gives the
    // mutation an honest result type.
    mutationFn: async (id: string) => {
      await deleteAlbum({ id });
    },
    onSuccess: () => client.invalidateQueries({ queryKey: qk.albums.all }),
  });
}
