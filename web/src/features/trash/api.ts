import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiJson } from '@/shared/api/tessera';
import { qk } from '@/shared/api/queries';

/**
 * Server state for the trash feature: `POST /api/trash/restore/assets` and
 * `POST /api/trash/restore` (contract §3). There is no emptying: the core
 * never deletes a file, and answers 403 if asked.
 *
 * The trash CONTENTS need no hooks here - the page reuses the timeline
 * queries with `isTrashed: true`. That is why every mutation below invalidates
 * `qk.timeline.all`: the trash grid IS a timeline query, and restoring an
 * asset changes both it and the main timeline in one stroke.
 */

export function useRestoreAssets() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (ids: string[]) =>
      apiJson<{ count: number }>('/api/trash/restore/assets', { method: 'POST', body: JSON.stringify({ ids }) }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.timeline.all }),
  });
}

export function useRestoreAll() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => apiJson<{ count: number }>('/api/trash/restore', { method: 'POST' }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.timeline.all }),
  });
}
