import { useState } from 'react';
import { TimelineGrid } from './TimelineGrid';
import { AssetViewer } from '@/features/viewer/AssetViewer';
import { MediaTypeFilter } from '@/shared/ui/MediaTypeFilter';
import { SelectionProvider } from '@/features/selection/SelectionContext';
import { SelectionBar } from '@/features/selection/SelectionBar';
import { AssetOrder, AssetVisibility } from '@immich/sdk';
import type { TimelineAsset, TimelineAssetType } from '@/shared/api/timeline';

/**
 * The main timeline.
 *
 * The media-type filter is powered by a LOCAL server patch — `assetType` on the
 * timeline endpoints, which stock Immich does not have. Because the filtering
 * happens in SQL rather than in the browser, switching to "Videos" on a
 * 1.6M-asset library returns only video buckets instead of downloading
 * everything and hiding most of it.
 *
 * Sort order rides the same query: the timeline DTO's `order` param
 * (asc/desc, verified against the fork's time-bucket.dto.ts and the live v3
 * server) flips both the bucket index and the assets within each bucket, so
 * "Oldest first" is one query-string change — the server does the sorting and
 * the grid just renders what arrives. Note that `order` is part of the query
 * key, so each direction caches independently; toggling back is instant.
 *
 * `SelectionProvider` wraps the page, not the app: a selection is a
 * transaction scoped to this view, and navigating away should end it. The
 * grid detects the provider and grows checkboxes; `SelectionBar` renders
 * nothing until something is ticked, so it mounts unconditionally.
 */
export function PhotosPage() {
  const [mediaType, setMediaType] = useState<TimelineAssetType | 'ALL'>('ALL');
  const [order, setOrder] = useState<AssetOrder>(AssetOrder.Desc);
  const [viewer, setViewer] = useState<{ assets: TimelineAsset[]; index: number } | null>(null);

  const query = {
    visibility: AssetVisibility.Timeline,
    withPartners: true,
    order,
    ...(mediaType === 'ALL' ? {} : { assetType: mediaType }),
  };

  return (
    <SelectionProvider>
      <div className="flex h-full flex-col">
        <div className="flex items-center gap-3 border-b border-line-soft px-4 py-2.5">
          <h1 className="text-sm font-medium text-ink-muted">Timeline</h1>
          <div className="ml-auto flex items-center gap-2">
            <OrderToggle value={order} onChange={setOrder} />
            <MediaTypeFilter value={mediaType} onChange={setMediaType} />
          </div>
        </div>

        <div className="min-h-0 flex-1">
          <TimelineGrid
            query={query}
            onSelect={(asset, all) => setViewer({ assets: all, index: all.indexOf(asset) })}
            emptyMessage={
              mediaType === 'VIDEO'
                ? 'No videos in this timeline.'
                : mediaType === 'IMAGE'
                  ? 'No photos in this timeline.'
                  : 'Nothing here yet.'
            }
          />
        </div>

        <SelectionBar />

        {viewer && (
          <AssetViewer
            assets={viewer.assets}
            index={viewer.index}
            onIndexChange={(index) => setViewer((current) => (current ? { ...current, index } : null))}
            onClose={() => setViewer(null)}
          />
        )}
      </div>
    </SelectionProvider>
  );
}

/**
 * Newest/oldest toggle. A single button that states the CURRENT order and
 * flips on click — with only two states, a segmented control would be
 * ceremony, and the arrow makes the direction legible at a glance.
 */
function OrderToggle({
  value,
  onChange,
}: {
  value: AssetOrder;
  onChange: (value: AssetOrder) => void;
}) {
  const newestFirst = value === AssetOrder.Desc;

  return (
    <button
      type="button"
      onClick={() => onChange(newestFirst ? AssetOrder.Asc : AssetOrder.Desc)}
      title={newestFirst ? 'Switch to oldest first' : 'Switch to newest first'}
      className="inline-flex items-center gap-1.5 rounded-lg border border-line bg-surface px-3 py-1.5 text-[13px] font-medium text-ink-muted transition-colors hover:text-ink"
    >
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" aria-hidden>
        {newestFirst ? <path d="M12 5v14m0 0 5-5m-5 5-5-5" /> : <path d="M12 19V5m0 0 5 5m-5-5-5 5" />}
      </svg>
      {newestFirst ? 'Newest first' : 'Oldest first'}
    </button>
  );
}
