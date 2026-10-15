import { useEffect } from 'react';
import { assetThumbnailUrl } from '@/shared/api/client';
import { MODAL_CANDIDATES, useCoverCandidates, useSetCover, type CoverCandidate } from './api';

/**
 * The cover picker modal.
 *
 * Opens with the sidecar's ranked candidates for one album; clicking a
 * candidate writes it as the album cover and LOCKS it — the automatic cover
 * worker respects the lock and will never override a manual choice (see
 * useSetCover in ./api.ts for the mechanics). The modal stays open after
 * setting so the result is visible, and closes on demand.
 *
 * One component serves both entry points — the album card's hover affordance
 * and the detail page's "Pick cover" action — so the ranking UI cannot drift
 * between them. The pages own their buttons; this owns everything after the
 * click.
 *
 * Failure is a first-class state here, not an afterthought: the sidecar is a
 * separate process on a separate port, and it being down must read as "this
 * one feature is unavailable", never as a broken albums page.
 */
export function CoverPickerModal({
  albumId,
  albumName,
  onClose,
}: {
  albumId: string;
  albumName: string;
  onClose: () => void;
}) {
  // Deliberately deeper than the hover strip: opening this is a request for
  // variety, not a second look at the same five.
  const candidates = useCoverCandidates(albumId, true, MODAL_CANDIDATES);
  const setCover = useSetCover(albumId);

  // Escape closes; the page behind must not scroll while the modal is up.
  // Same pattern as AssetViewer — worth keeping identical so every overlay in
  // the app behaves the same way.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      window.removeEventListener('keydown', onKey);
      document.body.style.overflow = previousOverflow;
    };
  }, [onClose]);

  return (
    <div className="fixed inset-0 z-50 grid place-items-center p-4">
      {/* The backdrop is a real button so closing works for keyboard and
          assistive tech, not just mouse users who happen to click outside. */}
      <button
        type="button"
        aria-label="Close cover picker"
        className="absolute inset-0 bg-black/60"
        onClick={onClose}
      />

      <div
        role="dialog"
        aria-modal="true"
        aria-label={`Pick a cover for ${albumName}`}
        className="relative flex max-h-[85vh] w-full max-w-2xl flex-col overflow-hidden rounded-lg border border-line bg-overlay shadow-2xl"
      >
        <header className="flex items-center gap-3 border-b border-line px-4 py-3">
          <div className="min-w-0">
            <h2 className="truncate text-base font-semibold text-ink">Pick a cover</h2>
            <p className="truncate text-xs text-ink-faint">{albumName}</p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="ml-auto rounded p-1.5 text-ink-faint hover:bg-raised hover:text-ink"
            aria-label="Close"
          >
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden>
              <path d="M18 6 6 18M6 6l12 12" />
            </svg>
          </button>
        </header>

        <div className="scrollbar-subtle min-h-0 flex-1 overflow-y-auto p-4">
          {candidates.isPending && <CandidatesSkeleton />}

          {candidates.isError && (
            <div className="py-8 text-center">
              <p className="text-sm text-ink-muted">The cover picker isn’t reachable.</p>
              <p className="mx-auto mt-1 max-w-md truncate text-xs text-ink-faint">
                {(candidates.error as Error).message}
              </p>
              <button
                type="button"
                onClick={() => void candidates.refetch()}
                className="mt-3 rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted hover:bg-raised hover:text-ink"
              >
                Try again
              </button>
            </div>
          )}

          {candidates.data &&
            (candidates.data.candidates.length === 0 ? (
              // The fast ranking is face-driven, so an album with no detected
              // faces legitimately produces zero candidates.
              <p className="py-8 text-center text-sm text-ink-muted">
                No ranked candidates for this album — the ranking works from faces, and none were
                found here.
              </p>
            ) : (
              <div className="grid grid-cols-3 gap-3 sm:grid-cols-5 lg:grid-cols-6">
                {candidates.data.candidates.map((candidate) => (
                  <CandidateTile
                    key={candidate.assetId}
                    candidate={candidate}
                    isCurrent={candidate.assetId === candidates.data.currentCoverAssetId}
                    isSetting={setCover.isPending && setCover.variables === candidate.assetId}
                    disabled={setCover.isPending}
                    onPick={() => setCover.mutate(candidate.assetId)}
                  />
                ))}
              </div>
            ))}
        </div>

        {/* The outcome stays visible until the modal closes: a mutation whose
            only evidence is a thumbnail changing somewhere else is too easy to
            miss. The lock is called out because it is permanent. */}
        {(setCover.isSuccess || setCover.isError) && (
          <footer className="border-t border-line px-4 py-2.5 text-sm">
            {setCover.isSuccess ? (
              <p className="text-success">Cover set and locked — the auto-picker won’t change it.</p>
            ) : (
              <p className="text-danger">
                Could not set the cover: {(setCover.error as Error).message}
              </p>
            )}
          </footer>
        )}
      </div>
    </div>
  );
}

function CandidateTile({
  candidate,
  isCurrent,
  isSetting,
  disabled,
  onPick,
}: {
  candidate: CoverCandidate;
  isCurrent: boolean;
  isSetting: boolean;
  disabled: boolean;
  onPick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onPick}
      disabled={disabled || isCurrent}
      title={candidate.fileName}
      className={`group relative aspect-square overflow-hidden rounded-md bg-raised ring-1 transition-all ${
        isCurrent
          ? 'cursor-default ring-2 ring-accent'
          : 'ring-line hover:ring-accent disabled:opacity-60'
      }`}
    >
      <img
        src={assetThumbnailUrl(candidate.assetId)}
        alt={`Candidate ${candidate.rank}: ${candidate.fileName}`}
        loading="lazy"
        decoding="async"
        className="h-full w-full object-cover"
      />

      <span className="absolute left-1.5 top-1.5 rounded bg-black/65 px-1.5 py-0.5 text-[11px] font-medium tabular-nums text-white">
        #{candidate.rank}
      </span>

      {isCurrent && (
        <span className="absolute bottom-1.5 left-1.5 rounded bg-accent px-1.5 py-0.5 text-[11px] font-semibold text-ink-inverse">
          Current
        </span>
      )}

      {isSetting && (
        <span className="absolute inset-0 grid place-items-center bg-black/50 text-xs font-medium text-white">
          Setting…
        </span>
      )}
    </button>
  );
}

function CandidatesSkeleton() {
  return (
    <div className="grid grid-cols-3 gap-3 sm:grid-cols-5 lg:grid-cols-6">
      {Array.from({ length: 8 }, (_, index) => (
        <div
          key={index}
          className="aspect-square animate-pulse rounded-md bg-raised"
          style={{ animationDelay: `${index * 40}ms` }}
        />
      ))}
    </div>
  );
}
