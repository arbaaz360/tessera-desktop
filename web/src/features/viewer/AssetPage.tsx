import { useNavigate, useParams } from '@tanstack/react-router';
import { AssetViewer } from './AssetViewer';
import { assetToTimelineAsset } from '@/shared/api/timeline';
import { useAssetInfo } from './info';

/**
 * Deep link to a single picture: /photos/{assetId}.
 *
 * The page is just the viewer over a one-asset list: closing it goes to the
 * timeline, and there is deliberately no prev/next - a deep link carries no
 * notion of "the surrounding pictures", and inventing one would surprise more
 * than it would help.
 */
export function AssetPage() {
  const { assetId } = useParams({ from: '/app/photos/$assetId' });
  const navigate = useNavigate();

  const { data, isPending, isError } = useAssetInfo(assetId, true);

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
          <h2 className="text-lg font-medium text-ink">This picture could not be opened</h2>
          <p className="mt-2 text-sm leading-relaxed text-ink-muted">
            It may have been removed from the library, or its folder may no longer be one of
            your roots. Check the Library page.
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
