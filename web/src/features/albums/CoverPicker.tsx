import { useEffect } from 'react';
import { TimelineGrid } from '@/features/timeline/TimelineGrid';
import { apiErrorMessage } from '@/shared/api/tessera';
import type { AlbumDto } from '@/shared/api/types';
import { useSetAlbumCover } from './api';

/**
 * Pick an album's cover from its own pictures.
 *
 * The album's grid, in a modal: the same virtualised `TimelineGrid` the
 * detail page uses, scoped to the album, with a click meaning "this one"
 * instead of "open the viewer". No ranking, no suggestions - the person
 * knows which picture stands for the folder, and on a library of a few
 * thousand the fastest way to it is to scroll.
 *
 * The modal stays open after setting so the result is visible, and closes on
 * demand. Escape closes; the page behind must not scroll while it is up -
 * the same pattern as every overlay in the app.
 */
export function CoverPicker({ album, onClose }: { album: AlbumDto; onClose: () => void }) {
  const setCover = useSetAlbumCover();

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
      <button
        type="button"
        aria-label="Close cover picker"
        className="absolute inset-0 bg-black/60"
        onClick={onClose}
      />

      <div
        role="dialog"
        aria-modal="true"
        aria-label={`Pick a cover for ${album.albumName}`}
        className="relative flex h-[85vh] w-full max-w-4xl flex-col overflow-hidden rounded-lg border border-line bg-overlay shadow-2xl"
      >
        <header className="flex items-center gap-3 border-b border-line px-4 py-3">
          <div className="min-w-0">
            <h2 className="truncate text-base font-semibold text-ink">Pick a cover</h2>
            <p className="truncate text-xs text-ink-faint">
              {album.albumName} · click a picture to make it the cover
            </p>
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

        <div className="min-h-0 flex-1">
          <TimelineGrid
            query={{ albumId: album.id, assetType: 'IMAGE' }}
            onSelect={(asset) => setCover.mutate({ id: album.id, assetId: asset.id })}
            emptyMessage="This album has no pictures to choose from."
          />
        </div>

        {(setCover.isPending || setCover.isSuccess || setCover.isError) && (
          <footer className="border-t border-line px-4 py-2.5 text-sm">
            {setCover.isPending ? (
              <p className="text-ink-muted">Setting the cover…</p>
            ) : setCover.isSuccess ? (
              <p className="text-success">Cover set.</p>
            ) : (
              <p className="text-danger">Could not set the cover: {apiErrorMessage(setCover.error)}</p>
            )}
          </footer>
        )}
      </div>
    </div>
  );
}
