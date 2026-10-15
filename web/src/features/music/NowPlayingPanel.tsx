import { useEffect, useState } from 'react';
import { useAssetMusic, useIdentifyAsset, type AssetMusic } from './api';

/**
 * The compact "now playing" card for one video — the successor to the pill
 * the injected script floated over the old viewer.
 *
 * Designed to be mounted blind: the asset viewer (wave 2) can render
 * `<NowPlayingPanel assetId={...} />` for EVERY asset without checking
 * anything first, because this component resolves to `null` in every case
 * where it has nothing true to say — the asset is a photo, the video has no
 * recognised music, the job is still queued, or the sidecar is down. A
 * wrapper that must not break the viewer cannot be allowed to have a visible
 * failure state.
 *
 * The API reports no per-match confidence (the provider's verdict is binary:
 * matched or not), so the card shows the match itself plus how many library
 * videos share it — the number that tells you whether this song is a one-off
 * or the soundtrack of a whole trip.
 */
export function NowPlayingPanel({ assetId }: { assetId: string }) {
  const { data } = useAssetMusic(assetId);
  const identify = useIdentifyAsset(assetId);

  /**
   * Re-identify resets the job to `pending` server-side, which would blank
   * `data` and unmount the card mid-interaction — the button the user just
   * pressed would vanish under their cursor. So the click snapshots the
   * current match and the card keeps rendering from it, labelled as working,
   * until the job settles: a fresh match takes over, anything else clears
   * the snapshot and the card bows out.
   */
  const [requeuedFrom, setRequeuedFrom] = useState<AssetMusic | null>(null);

  const settled =
    data?.state === 'matched' || data?.state === 'no_match' || data?.state === 'failed';
  useEffect(() => {
    if (settled) setRequeuedFrom(null);
  }, [settled]);

  const row = data?.state === 'matched' ? data : requeuedFrom;
  if (!row || !row.track) return null;
  const track = row.track;

  const working = requeuedFrom !== null || identify.isPending;

  return (
    <div
      className={`flex max-w-md items-center gap-3 rounded-lg border border-line bg-surface p-3 transition-opacity ${
        working ? 'opacity-60' : ''
      }`}
    >
      {track.artworkUrl ? (
        <img
          src={track.artworkUrl}
          alt=""
          referrerPolicy="no-referrer"
          className="h-11 w-11 shrink-0 rounded-md bg-raised object-cover ring-1 ring-line"
        />
      ) : (
        <div className="grid h-11 w-11 shrink-0 place-items-center rounded-md bg-raised text-ink-faint ring-1 ring-line" aria-hidden>
          <IconNote />
        </div>
      )}

      <div className="min-w-0 flex-1">
        <p className="truncate text-sm font-semibold text-ink" title={track.title}>
          {track.title}
        </p>
        <p className="truncate text-xs text-ink-muted">
          {track.artist || 'Unknown artist'}
          {row.sameTrackCount > 1 && ` · in ${row.sameTrackCount.toLocaleString()} videos`}
        </p>
      </div>

      <button
        type="button"
        disabled={working}
        onClick={() => {
          setRequeuedFrom(row);
          identify.mutate();
        }}
        className="shrink-0 rounded-md border border-line bg-surface px-2.5 py-1.5 text-xs font-medium text-ink-muted transition-colors hover:text-ink disabled:cursor-wait"
        title="Run recognition on this video again"
      >
        {working ? 'Listening…' : 'Detect again'}
      </button>
    </div>
  );
}

function IconNote() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <path d="M9 18V6l11-2v12" />
      <circle cx="6.5" cy="18" r="2.5" />
      <circle cx="17.5" cy="16" r="2.5" />
    </svg>
  );
}
