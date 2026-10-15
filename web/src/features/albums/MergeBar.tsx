import { useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import type { AlbumResponseDto } from '@immich/sdk';
import { qk } from '@/shared/api/queries';
import { describeMerge, mergeAlbums, type MergeOutcome, type MergeProgress } from './merge';
import { MoveAlbumDialog } from './MoveAlbumDialog';

/**
 * The floating bar shown while albums are selected for merging.
 *
 * Selection persists across filtering — which is the point. Albums to merge are
 * usually far apart in a 4,865-album grid, so the intended flow is: filter by
 * name, tick one, clear the filter, filter again, tick another. The bar stays
 * visible throughout and lists what is currently held, because a selection you
 * cannot see is a selection you cannot trust.
 *
 * Choosing the KEEPER is a deliberate second step rather than "the first one
 * you ticked". Which album survives determines which cover, which shares and
 * which cover-policy lock survive with it, so it is not a detail to infer.
 */
export function MergeBar({
  selected,
  onClear,
  onRemove,
}: {
  selected: AlbumResponseDto[];
  onClear: () => void;
  onRemove: (albumId: string) => void;
}) {
  const queryClient = useQueryClient();
  const [keeperId, setKeeperId] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [progress, setProgress] = useState<MergeProgress | null>(null);
  const [outcome, setOutcome] = useState<MergeOutcome | null>(null);

  // A merge deletes albums, so running it twice is not merely wasteful: the
  // second pass would try to delete albums the first already removed. A ref,
  // not state, because two clicks in the same tick see the same stale state
  // but the same ref — which is exactly what a double-click produces.
  //
  // It MUST sit above the early return below. Placed after it, this hook ran
  // only when something was selected, so the hook count changed between
  // renders and React threw #310 the instant an album was ticked.
  const inFlight = useRef(false);

  // Moving is offered for a SINGLE album, merging for two or more, so the two
  // actions never compete for the same selection.
  const [moving, setMoving] = useState(false);

  if (selected.length === 0 && !outcome) return null;

  const keeper = selected.find((album) => album.id === keeperId) ?? null;
  const sources = keeper ? selected.filter((album) => album.id !== keeper.id) : [];
  const running = progress !== null;

  const run = async () => {
    if (!keeper || inFlight.current) return;
    inFlight.current = true;
    setConfirming(false);
    setProgress({ albumName: '', done: 0, total: sources.length });

    const result = await mergeAlbums(keeper, sources, setProgress).finally(() => {
      inFlight.current = false;
    });

    setProgress(null);
    setOutcome(result);
    setKeeperId(null);
    onClear();
    void queryClient.invalidateQueries({ queryKey: qk.albums.all });
    void queryClient.invalidateQueries({ queryKey: qk.timeline.all });
  };

  return (
    <div className="pointer-events-none fixed inset-x-0 bottom-6 z-40 flex justify-center px-4">
      <div className="pointer-events-auto w-full max-w-3xl rounded-xl border border-line bg-overlay p-3 shadow-2xl">
        {outcome ? (
          <MergeResult outcome={outcome} onDismiss={() => setOutcome(null)} />
        ) : running ? (
          <p className="px-1 text-sm text-ink">
            Merging {progress.done + 1} of {progress.total}
            {progress.albumName && <span className="text-ink-muted"> — “{progress.albumName}”</span>}
            …
          </p>
        ) : (
          <>
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-sm font-medium tabular-nums text-ink">
                {selected.length} selected
              </span>

              <div className="flex min-w-0 flex-1 flex-wrap gap-1.5">
                {selected.map((album) => (
                  <button
                    key={album.id}
                    type="button"
                    onClick={() => onRemove(album.id)}
                    title={`Remove “${album.albumName}” from the selection`}
                    className={`inline-flex max-w-[14rem] items-center gap-1 rounded-md border px-2 py-1 text-xs transition-colors ${
                      album.id === keeperId
                        ? 'border-accent bg-accent-muted text-accent'
                        : 'border-line text-ink-muted hover:text-ink'
                    }`}
                  >
                    <span className="truncate">{album.albumName}</span>
                    <span className="shrink-0 tabular-nums opacity-70">
                      {album.assetCount.toLocaleString()}
                    </span>
                  </button>
                ))}
              </div>

              <button
                type="button"
                onClick={onClear}
                className="rounded-md p-1.5 text-ink-muted hover:bg-raised hover:text-ink"
                aria-label="Clear selection"
              >
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
                  <path d="M18 6 6 18M6 6l12 12" />
                </svg>
              </button>
            </div>

            {/* Both actions live on the same row, because they answer
                different questions about the same selection: merge asks WHICH
                ALBUM survives, move asks WHICH ACCOUNT it lives in. The first
                version showed move only for a single selection, which meant
                anyone with two albums ticked had no way to move them at all. */}
            <div className="mt-2.5 flex flex-wrap items-center gap-2 border-t border-line-soft pt-2.5">
              <button
                type="button"
                onClick={() => setMoving(true)}
                className="inline-flex items-center gap-1.5 rounded-md border border-line px-3 py-1.5 text-sm font-medium text-ink-muted transition-colors hover:bg-raised hover:text-ink"
              >
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden>
                  <path d="M3 8h14M13 4l4 4-4 4M21 16H7M11 12l-4 4 4 4" />
                </svg>
                Move {selected.length === 1 ? '' : `${selected.length} `}to the other account
              </button>

              {selected.length === 1 && (
                <span className="text-xs text-ink-faint">
                  Tick a second album to merge instead.
                </span>
              )}
            </div>

            {selected.length >= 2 && (
              <div className="mt-2.5 flex flex-wrap items-center gap-2 border-t border-line-soft pt-2.5">
                <label className="flex items-center gap-1.5 text-sm text-ink-faint">
                  Keep
                  <select
                    value={keeperId ?? ''}
                    onChange={(event) => setKeeperId(event.target.value || null)}
                    className="max-w-[16rem] rounded-md border border-line bg-surface px-2 py-1.5 text-sm text-ink focus:border-accent focus:outline-none"
                  >
                    <option value="">Choose which album survives…</option>
                    {selected.map((album) => (
                      <option key={album.id} value={album.id}>
                        {album.albumName} ({album.assetCount.toLocaleString()})
                      </option>
                    ))}
                  </select>
                </label>

                <button
                  type="button"
                  disabled={!keeper}
                  onClick={() => setConfirming(true)}
                  className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-ink-inverse transition-colors hover:bg-accent-hover disabled:opacity-40"
                >
                  Merge {sources.length || ''} into “{keeper?.albumName ?? '…'}”
                </button>
              </div>
            )}
          </>
        )}
      </div>

      {/* `pointer-events-auto` on the dialog is load-bearing: it renders inside
          the bar's `pointer-events-none` positioning wrapper, and without it the
          Cancel and Merge buttons are visible but UNCLICKABLE — which made the
          merge look hung when in fact no click ever reached it. */}
      {moving && selected.length > 0 && (
        <MoveAlbumDialog
          albums={selected}
          onClose={() => {
            setMoving(false);
            // The albums have left this account, so holding them selected
            // would offer actions against things that are no longer here.
            onClear();
          }}
        />
      )}

      {confirming && keeper && (
        <div className="pointer-events-auto fixed inset-0 z-50 grid place-items-center p-4">
          <button
            type="button"
            aria-label="Cancel merge"
            className="absolute inset-0 bg-black/60"
            onClick={() => setConfirming(false)}
          />
          <div
            role="dialog"
            aria-modal="true"
            aria-label="Confirm album merge"
            className="relative w-full max-w-md rounded-lg border border-line bg-overlay p-4 shadow-2xl"
          >
            <h2 className="text-base font-semibold text-ink">Merge albums?</h2>
            <p className="mt-2 text-sm leading-relaxed text-ink-muted">
              {describeMerge(keeper, sources)}
            </p>
            <div className="mt-4 flex justify-end gap-2">
              <button
                type="button"
                onClick={() => setConfirming(false)}
                className="rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted hover:bg-raised hover:text-ink"
              >
                Cancel
              </button>
              <button
                type="button"
                disabled={running}
                onClick={() => void run()}
                className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-ink-inverse hover:bg-accent-hover disabled:opacity-50"
              >
                {running ? 'Merging…' : 'Merge'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function MergeResult({ outcome, onDismiss }: { outcome: MergeOutcome; onDismiss: () => void }) {
  return (
    <div className="flex flex-wrap items-start gap-3 px-1">
      <div className="min-w-0 flex-1">
        <p className="text-sm text-ink">
          {outcome.moved.toLocaleString()} item{outcome.moved === 1 ? '' : 's'} added to “
          {outcome.keeperName}”, {outcome.absorbed} album
          {outcome.absorbed === 1 ? '' : 's'} removed.
        </p>
        {outcome.failures.map((failure) => (
          <p key={failure.name} className="mt-1 text-xs text-danger">
            “{failure.name}” kept: {failure.message}
          </p>
        ))}
      </div>
      <button
        type="button"
        onClick={onDismiss}
        className="rounded-md border border-line px-2.5 py-1 text-xs text-ink-muted hover:bg-raised hover:text-ink"
      >
        Dismiss
      </button>
    </div>
  );
}
