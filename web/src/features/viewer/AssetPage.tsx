import { useQuery } from '@tanstack/react-query';
import { useNavigate, useParams } from '@tanstack/react-router';
import { getAssetInfo } from '@immich/sdk';
import { AssetViewer } from './AssetViewer';
import { assetToTimelineAsset } from '@/shared/api/timeline';

/**
 * Deep link to a single asset: /photos/{assetId}.
 *
 * This route exists for links that arrive from OUTSIDE the app's own
 * navigation. The reverse-face-search sidecar links its results as
 * `/photos/{assetId}` — the URL shape the old Svelte web understood — and a
 * copied address bar URL is the natural way to share one photo on the LAN.
 * Without this route, both landed on "Not Found".
 *
 * The page is just the viewer over a one-asset list: closing it goes to the
 * timeline, and there is deliberately no prev/next — a deep link carries no
 * notion of "the surrounding photos", and inventing one (nearest by date?)
 * would surprise more than it would help.
 */
export function AssetPage() {
  const { assetId } = useParams({ from: '/authed/photos/$assetId' });
  const navigate = useNavigate();

  const { data, isPending, isError } = useQuery({
    queryKey: ['asset', 'detail', assetId],
    queryFn: () => getAssetInfo({ id: assetId }),
    staleTime: 60_000,
    retry: false,
  });

  if (isPending) {
    return (
      <div className="grid h-full place-items-center">
        <div className="h-24 w-24 animate-pulse rounded-lg bg-raised" />
      </div>
    );
  }

  if (isError || !data) {
    return (
      <div className="grid h-full place-items-center px-6 text-center">
        <div className="max-w-md">
          <h2 className="text-lg font-medium text-ink">This photo could not be opened</h2>
          <p className="mt-2 text-sm leading-relaxed text-ink-muted">
            It may have been deleted, or it may belong to another account — assets are only
            visible to the account that owns them, so check the account menu if you expected
            to see it.
          </p>
        </div>
      </div>
    );
  }

  return (
    <AssetViewer
      assets={[assetToTimelineAsset(data)]}
      index={0}
      onIndexChange={() => {}}
      onClose={() => void navigate({ to: '/photos' })}
    />
  );
}
