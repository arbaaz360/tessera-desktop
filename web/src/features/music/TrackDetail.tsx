import { useState } from 'react';
import { assetOriginalUrl, assetThumbnailUrl } from '@/shared/api/client';
import { TRACK_PAGE_SIZE, useTrackVideos, type TrackVideo } from './api';
import { formatClockMs } from './format';
import { TrackCard } from './TrackCard';

/**
 * One track and every library video it was recognised in.
 *
 * The endpoint is paged (max 200 per request); we page at 100 with prev/next
 * rather than virtualising, because unlike the 4,878-album grid this list is
 * bounded by how many videos can share one song — pagination state here is
 * two integers, and the virtualiser would be solving a problem this screen
 * does not have.
 */
export function TrackDetail({ trackKey, onBack }: { trackKey: string; onBack: () => void }) {
  const [page, setPage] = useState(1);
  const { data, isPending, isError, error, isPlaceholderData } = useTrackVideos(trackKey, page);

  return (
    <div className="scrollbar-subtle h-full overflow-y-auto">
      <div className="mx-auto max-w-5xl space-y-5 px-6 py-6">
        <header className="flex items-center gap-3">
          <button type="button" onClick={onBack} className="text-sm text-ink-faint transition-colors hover:text-ink">
            ← Music
          </button>
        </header>

        {isPending && <TrackDetailSkeleton />}
        {isError && <p className="py-8 text-center text-sm text-ink-muted">Could not load this track. {error.message}</p>}

        {data && (
          <>
            <TrackCard
              track={data.track}
              action={
                <span className="shrink-0 text-sm tabular-nums text-ink-faint">
                  {data.total.toLocaleString()} {data.total === 1 ? 'video' : 'videos'}
                </span>
              }
            />

            {data.items.length === 0 ? (
              <p className="py-8 text-center text-sm text-ink-muted">
                No videos of yours currently match this track.
              </p>
            ) : (
              <div
                // Dim the stale page while the next one loads — visible feedback
                // without unmounting the grid (keepPreviousData's other half).
                className={`grid gap-4 transition-opacity ${isPlaceholderData ? 'opacity-50' : ''}`}
                style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))' }}
              >
                {data.items.map((item) => (
                  <VideoCard key={item.id} item={item} />
                ))}
              </div>
            )}

            {data.total > TRACK_PAGE_SIZE && (
              <nav className="flex items-center justify-center gap-4 py-2" aria-label="Pages">
                <PagerButton
                  label="← Previous"
                  disabled={page <= 1}
                  onClick={() => setPage((current) => current - 1)}
                />
                <span className="text-sm tabular-nums text-ink-faint">
                  Page {page} of {Math.max(1, Math.ceil(data.total / TRACK_PAGE_SIZE))}
                </span>
                <PagerButton
                  label="Next →"
                  disabled={page >= Math.ceil(data.total / TRACK_PAGE_SIZE)}
                  onClick={() => setPage((current) => current + 1)}
                />
              </nav>
            )}
          </>
        )}
      </div>
    </div>
  );
}

/**
 * WAVE 1: the card opens the original file in a new tab — the browser streams
 * and plays the video directly off the media volume. The app has no asset
 * deep-link route yet (the viewer is an overlay owned by each grid page), so
 * this is the honest interim link; wave 2 replaces it with in-app viewing.
 */
function VideoCard({ item }: { item: TrackVideo }) {
  // Tessera omits null fields from JSON, so an unknown duration arrives absent.
  const duration = item.durationMs == null ? null : formatClockMs(item.durationMs);
  const created = item.takenAt ? new Date(item.takenAt) : null;

  return (
    <a
      href={assetOriginalUrl(item.id)}
      target="_blank"
      rel="noreferrer"
      className="group flex min-w-0 flex-col gap-1.5 focus-visible:outline-none"
      title={item.fileName}
    >
      <div className="relative aspect-square overflow-hidden rounded-lg bg-raised ring-1 ring-line transition-all duration-200 group-hover:ring-accent group-focus-visible:ring-2 group-focus-visible:ring-accent">
        {/* `thumbnail`, never `preview` — with preview generation disabled,
            preview would serve the multi-megabyte original into a 160px cell. */}
        <img
          src={assetThumbnailUrl(item.id)}
          alt=""
          loading="lazy"
          decoding="async"
          className="h-full w-full object-cover"
        />
        {duration && (
          <span className="absolute bottom-1.5 right-1.5 rounded bg-black/60 px-1.5 py-0.5 text-[11px] font-medium tabular-nums text-white">
            {duration}
          </span>
        )}
      </div>
      <div className="min-w-0">
        <p className="truncate text-xs font-medium text-ink">{item.fileName}</p>
        <p className="text-[11px] tabular-nums text-ink-faint">
          {created && !Number.isNaN(created.getTime()) ? created.toLocaleDateString() : ''}
        </p>
      </div>
    </a>
  );
}

function PagerButton({
  label,
  disabled,
  onClick,
}: {
  label: string;
  disabled: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={onClick}
      className="rounded-md border border-line bg-surface px-3 py-1.5 text-sm text-ink-muted transition-colors hover:text-ink disabled:cursor-not-allowed disabled:opacity-40"
    >
      {label}
    </button>
  );
}

function TrackDetailSkeleton() {
  return (
    <div className="space-y-5">
      <div className="h-24 animate-pulse rounded-lg bg-raised" />
      <div className="grid gap-4" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))' }}>
        {Array.from({ length: 8 }, (_, index) => (
          <div
            key={index}
            className="aspect-square animate-pulse rounded-lg bg-raised"
            style={{ animationDelay: `${index * 35}ms` }}
          />
        ))}
      </div>
    </div>
  );
}
