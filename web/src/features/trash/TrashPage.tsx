import { useMemo, useState } from 'react';
import { TimelineGrid } from '@/features/timeline/TimelineGrid';
import { AssetViewer } from '@/features/viewer/AssetViewer';
import { SelectionProvider, useSelection } from '@/features/selection/SelectionContext';
import { ConfirmDialog } from '@/features/selection/ConfirmDialog';
import { useTimeBuckets } from '@/shared/api/queries';
import { apiErrorMessage } from '@/shared/api/tessera';
import { useRestoreAll, useRestoreAssets } from './api';
import type { TimelineAsset } from '@/shared/api/timeline';

/**
 * Trash: deleted assets awaiting permanent removal or restore.
 *
 * The page is `TimelineGrid` with `isTrashed: true` — the fork's timeline
 * endpoints accept that filter directly (verified against the live v3
 * sandbox), so the trash gets month grouping, virtualisation and the
 * justified layout for free instead of a second, lesser grid.
 *
 * Selection is reused too, but NOT the standard `SelectionBar`: favoriting
 * or albuming a deleted asset is not a coherent action, and "delete" from
 * inside the trash doubly so. The provider is deliberately separable from
 * the bar — this page mounts the same provider with its own restore bar,
 * which is the payoff for keeping them apart.
 *
 * There is no "Empty trash". Originals are never deleted by this system -
 * the house rule - so the trash is a list of hidden pictures, and the only
 * way out of it is back into the library. "Restore all" gets a plain confirm.
 */
export function TrashPage() {
  return (
    <SelectionProvider>
      <TrashPageBody />
    </SelectionProvider>
  );
}

/** Split out so the page body can call `useSelection` under the provider. */
function TrashPageBody() {
  const selection = useSelection();
  const [viewer, setViewer] = useState<{ assets: TimelineAsset[]; index: number } | null>(null);
  const [confirming, setConfirming] = useState<'none' | 'restore-all'>('none');

  const query = useMemo(() => ({ isTrashed: true }), []);

  // The grid fetches this same index, so the sum is a cache read, not a
  // second request. It powers the header count and the confirm copy.
  const { data: buckets } = useTimeBuckets(query);
  const totalCount = useMemo(
    () => buckets?.reduce((sum, bucket) => sum + bucket.count, 0) ?? 0,
    [buckets],
  );

  const restoreSelected = useRestoreAssets();
  const restoreAll = useRestoreAll();

  const error = restoreSelected.error ?? restoreAll.error ?? null;

  return (
    <div className="flex h-full flex-col">
      <div className="flex flex-wrap items-center gap-3 border-b border-line-soft px-4 py-2.5">
        <h1 className="text-sm font-medium text-ink-muted">Trash</h1>
        {totalCount > 0 && (
          <span className="text-sm tabular-nums text-ink-faint">
            {totalCount.toLocaleString()} item{totalCount === 1 ? '' : 's'}
          </span>
        )}

        <span className="text-sm text-ink-faint">
          Nothing here is ever deleted; the files stay where they are. Restore what you want back.
        </span>

        <div className="ml-auto flex items-center gap-2">
          <button
            type="button"
            onClick={() => setConfirming('restore-all')}
            disabled={totalCount === 0 || restoreAll.isPending}
            className="rounded-lg border border-line bg-surface px-3 py-1.5 text-[13px] font-medium text-ink-muted transition-colors hover:text-ink disabled:opacity-50"
          >
            Restore all
          </button>
        </div>
      </div>

      {error != null && (
        <p className="border-b border-danger/30 bg-danger/10 px-4 py-2 text-sm text-danger">
          {apiErrorMessage(error)}
        </p>
      )}

      <div className="min-h-0 flex-1">
        <TimelineGrid
          query={query}
          onSelect={(asset, all) => setViewer({ assets: all, index: all.indexOf(asset) })}
          emptyMessage="Trash is empty."
        />
      </div>

      {/* This page's own floating bar: restore is the only bulk action that
          makes sense on already-deleted assets. */}
      {selection.active && (
        <div className="pointer-events-none fixed inset-x-0 bottom-6 z-40 flex justify-center px-4">
          <div className="pointer-events-auto flex items-center gap-2 rounded-xl border border-line bg-overlay px-3 py-2 shadow-xl">
            <span className="text-sm font-medium tabular-nums text-ink">
              {selection.count.toLocaleString()} selected
            </span>
            <button
              type="button"
              disabled={restoreSelected.isPending}
              onClick={() =>
                restoreSelected.mutate([...selection.selected], {
                  onSuccess: () => selection.clear(),
                })
              }
              className="rounded-lg bg-accent px-3 py-1.5 text-sm font-medium text-ink-inverse transition-colors hover:bg-accent-hover disabled:cursor-wait disabled:opacity-50"
            >
              {restoreSelected.isPending ? 'Restoring…' : 'Restore selected'}
            </button>
            <button
              type="button"
              onClick={selection.clear}
              className="rounded-lg p-2 text-ink-muted transition-colors hover:bg-raised hover:text-ink"
              aria-label="Clear selection"
            >
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
                <path d="M18 6 6 18M6 6l12 12" />
              </svg>
            </button>
          </div>
        </div>
      )}

      {confirming === 'restore-all' && (
        <ConfirmDialog
          title="Restore everything?"
          confirmLabel="Restore all"
          busy={restoreAll.isPending}
          onConfirm={() =>
            restoreAll.mutate(undefined, {
              onSuccess: () => {
                setConfirming('none');
                selection.clear();
              },
            })
          }
          onCancel={() => setConfirming('none')}
        >
          All {totalCount.toLocaleString()} item{totalCount === 1 ? '' : 's'} will return to
          the library, exactly where they were.
        </ConfirmDialog>
      )}

      {viewer && (
        <AssetViewer
          assets={viewer.assets}
          index={viewer.index}
          onIndexChange={(index) => setViewer((current) => (current ? { ...current, index } : null))}
          onClose={() => setViewer(null)}
        />
      )}
    </div>
  );
}
