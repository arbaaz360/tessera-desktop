import type { TrackSummary } from './api';

/**
 * One identified track: artwork, title, artist, album.
 *
 * Shared by the upload result and the track-detail header, which is why it
 * takes an optional `action` slot instead of knowing what happens next — the
 * upload flow puts a "view matching videos" button there, the detail view
 * puts the match count. The card describes a track; the caller decides what
 * a track is FOR on that screen.
 */
export function TrackCard({
  track,
  action,
}: {
  track: TrackSummary;
  action?: React.ReactNode;
}) {
  return (
    <div className="flex items-center gap-4 rounded-lg border border-line bg-surface p-4">
      {track.artworkUrl ? (
        // Artwork comes from the recognition provider's CDN — the one external
        // image on the page. `referrerPolicy` keeps our host out of their logs.
        <img
          src={track.artworkUrl}
          alt=""
          referrerPolicy="no-referrer"
          className="h-16 w-16 shrink-0 rounded-md bg-raised object-cover ring-1 ring-line"
        />
      ) : (
        <div className="grid h-16 w-16 shrink-0 place-items-center rounded-md bg-raised text-ink-faint ring-1 ring-line" aria-hidden>
          <IconNote />
        </div>
      )}

      <div className="min-w-0 flex-1">
        <p className="truncate text-base font-semibold text-ink" title={track.title}>
          {track.title}
        </p>
        <p className="truncate text-sm text-ink-muted" title={track.artist}>
          {track.artist || 'Unknown artist'}
        </p>
        {track.album && (
          <p className="truncate text-xs text-ink-faint" title={track.album}>
            {track.album}
          </p>
        )}
      </div>

      {action}
    </div>
  );
}

function IconNote() {
  return (
    <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <path d="M9 18V6l11-2v12" />
      <circle cx="6.5" cy="18" r="2.5" />
      <circle cx="17.5" cy="16" r="2.5" />
    </svg>
  );
}
