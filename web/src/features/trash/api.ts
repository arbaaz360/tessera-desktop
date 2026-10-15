import { useMutation, useQueryClient } from '@tanstack/react-query';
import { restoreAssets, restoreTrash } from '@immich/sdk';
import { qk } from '@/shared/api/queries';

/**
 * Server state for the trash feature.
 *
 * Feature-local, following the pattern set by `features/admin/api.ts`:
 * nothing outside this page restores from the trash. There is no emptying:
 * Tessera never deletes an original, and answers 403 if asked.
 *
 * The trash CONTENTS need no hooks here at all — the page reuses the timeline
 * queries with `isTrashed: true`, which the v3 fork's `TimeBucketDto` accepts
 * (verified live: trashing an asset moved it from the normal bucket query to
 * the `isTrashed=true` one). That is why every mutation below invalidates
 * `qk.timeline.all`: the trash grid IS a timeline query, and restoring an
 * asset changes both it and the main timeline in one stroke.
 */

export function useRestoreAssets() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (ids: string[]) => restoreAssets({ bulkIdsDto: { ids } }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.timeline.all }),
  });
}

export function useRestoreAll() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => restoreTrash(),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.timeline.all }),
  });
}
