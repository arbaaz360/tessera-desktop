import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from '@tanstack/react-router';
import { useAlbum } from '@/shared/api/queries';
import { apiErrorMessage } from '@/shared/api/tessera';
import type { AlbumDto } from '@/shared/api/types';
import { TimelineGrid } from '@/features/timeline/TimelineGrid';
import { AssetViewer } from '@/features/viewer/AssetViewer';
import { MediaTypeFilter, type MediaTypeValue } from '@/shared/ui/MediaTypeFilter';
import { SelectionProvider } from '@/features/selection/SelectionContext';
import { SelectionBar } from '@/features/selection/SelectionBar';
import type { TimelineAsset } from '@/shared/api/timeline';
import { CoverPicker } from './CoverPicker';
import { AddButtons, EmptyLibraryHint } from '@/features/import/AddButtons';
import { useDeleteAlbum, useRenameAlbum } from './api';

/**
 * A single album.
 *
 * Reuses `TimelineGrid` rather than reimplementing a grid - an album is just a
 * timeline with `albumId` in the query, so it inherits bucketing,
 * virtualisation and lazy loading for free.
 *
 * The album's metadata comes from the cached album LIST via `useAlbum`, not an
 * individual fetch. The grid fetches assets itself, in month-sized pieces.
 *
 * Management lives here too: rename inline on the title, delete behind a
 * confirm dialog, and the cover picker. The rename rides the optimistic
 * mutation in `api.ts`, so the title updates the instant Enter is pressed and
 * only snaps back if the core refuses.
 */
export function AlbumDetailPage() {
  const { albumId } = useParams({ from: '/app/albums/$albumId' });

  const { data: album, isPending, isError } = useAlbum(albumId);
  const [mediaType, setMediaType] = useState<MediaTypeValue>('ALL');
  const [viewer, setViewer] = useState<{ assets: TimelineAsset[]; index: number } | null>(null);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [pickingCover, setPickingCover] = useState(false);

  const query = {
    albumId,
    ...(mediaType === 'ALL' ? {} : { assetType: mediaType }),
  };

  if (isPending) {
    return (
      <div className="p-6">
        <div className="h-8 w-64 animate-pulse rounded bg-raised" />
      </div>
    );
  }

  if (isError || !album) {
    return <p className="p-8 text-center text-ink-muted">Could not load that album.</p>;
  }

  const target = { albumId: album.id, albumName: album.albumName };

  // The whole page is a drop target for this album: pictures dragged onto it
  // from a folder are added here (features/import/DropZone).
  return (
    <div
      className="flex h-full flex-col"
      data-drop-album-id={album.id}
      data-drop-album-name={album.albumName}
      data-drop-page=""
    >
      <header className="border-b border-line-soft px-4 py-3">
        <Link to="/albums" className="text-sm text-ink-faint hover:text-ink">
          ← Albums
        </Link>

        <div className="mt-1.5 flex flex-wrap items-center gap-3">
          <div className="min-w-0">
            <AlbumTitle album={album} />
            <p className="text-sm tabular-nums text-ink-faint">
              {album.assetCount.toLocaleString()} items
              {album.description ? ` · ${album.description}` : ''}
            </p>
          </div>

          <div className="ml-auto flex flex-wrap items-center gap-2">
            {/* Searching in this album is the top bar's box: on an album page
                it is scoped to the album already (GlobalSearch). A second box
                here said the same thing twice. */}
            <MediaTypeFilter value={mediaType} onChange={setMediaType} />

            <AddButtons target={target} />

            <button
              type="button"
              onClick={() => setPickingCover(true)}
              disabled={album.assetCount === 0}
              title="Choose which picture stands for this album"
              className="inline-flex items-center gap-1.5 rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted transition-colors hover:bg-raised hover:text-ink disabled:cursor-not-allowed disabled:opacity-50"
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden>
                <rect x="3" y="3" width="18" height="18" rx="2" />
                <circle cx="9" cy="9" r="2" />
                <path d="m21 15-5-5L5 21" />
              </svg>
              Pick cover
            </button>

            <button
              type="button"
              onClick={() => setConfirmingDelete(true)}
              className="inline-flex items-center gap-1.5 rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted transition-colors hover:border-danger hover:text-danger"
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden>
                <path d="M3 6h18M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2m3 0v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6" />
              </svg>
              Delete
            </button>
          </div>
        </div>
      </header>

      {/* Same selection scope as the main timeline: the provider wraps this
          view only, so navigating away ends the selection. */}
      <SelectionProvider>
        <div className="min-h-0 flex-1">
          <TimelineGrid
            query={query}
            onSelect={(asset, all) => setViewer({ assets: all, index: all.indexOf(asset) })}
            emptyMessage={
              mediaType === 'ALL' ? (
                <EmptyLibraryHint
                  target={target}
                  lead="This album is empty. Drop pictures here, add them below, or select pictures in the library and add them to it."
                />
              ) : (
                `No ${mediaType === 'IMAGE' ? 'pictures' : 'videos'} in this album.`
              )
            }
          />
        </div>

        <SelectionBar albumId={album.id} />
      </SelectionProvider>

      {viewer && (
        <AssetViewer
          assets={viewer.assets}
          index={viewer.index}
          onIndexChange={(index) => setViewer((current) => (current ? { ...current, index } : null))}
          onClose={() => setViewer(null)}
        />
      )}

      {pickingCover && <CoverPicker album={album} onClose={() => setPickingCover(false)} />}

      {confirmingDelete && (
        <DeleteAlbumDialog album={album} onClose={() => setConfirmingDelete(false)} />
      )}
    </div>
  );
}

/**
 * The title, doubling as the rename control.
 *
 * Clicking the name swaps in an input; the ONLY commit path is blur. Enter and
 * Escape both just blur the input - Enter after setting nothing, Escape after
 * setting a ref that tells the blur handler to discard. Funnelling everything
 * through one handler avoids the double-commit bug where Enter mutates, the
 * state change unmounts the input, and the resulting blur mutates again.
 */
function AlbumTitle({ album }: { album: AlbumDto }) {
  const rename = useRenameAlbum();
  const [draft, setDraft] = useState<string | null>(null);
  const cancelled = useRef(false);

  const commit = () => {
    const wasCancelled = cancelled.current;
    cancelled.current = false;
    const next = draft?.trim();
    setDraft(null);
    if (wasCancelled || !next || next === album.albumName) return;
    rename.mutate({ id: album.id, albumName: next });
  };

  if (draft !== null) {
    return (
      <input
        autoFocus
        type="text"
        value={draft}
        onChange={(event) => setDraft(event.target.value)}
        onBlur={commit}
        onKeyDown={(event) => {
          if (event.key === 'Enter') event.currentTarget.blur();
          if (event.key === 'Escape') {
            cancelled.current = true;
            event.currentTarget.blur();
          }
        }}
        aria-label="Album name"
        className="w-full max-w-md rounded-md border border-accent bg-surface px-2 py-0.5 text-xl font-semibold tracking-tight text-ink focus:outline-none"
      />
    );
  }

  return (
    <>
      <h1 className="min-w-0 text-xl font-semibold tracking-tight text-ink">
        <button
          type="button"
          onClick={() => setDraft(album.albumName)}
          title="Rename album"
          className="group flex min-w-0 max-w-full items-center gap-2 text-left"
        >
          <span className="truncate">{album.albumName}</span>
          <svg
            width="14"
            height="14"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            aria-hidden
            className="shrink-0 text-ink-faint opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100"
          >
            <path d="M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z" />
          </svg>
        </button>
      </h1>

      {rename.isError && (
        <p className="text-xs text-danger">Rename failed: {apiErrorMessage(rename.error)}</p>
      )}
    </>
  );
}

/**
 * Delete confirmation.
 *
 * The dialog names the album and its size because "Delete" on a generic
 * confirm is how the wrong album dies. It also says out loud what deleting an
 * album does NOT do: an album is a grouping, and removing it never touches
 * the files.
 */
function DeleteAlbumDialog({ album, onClose }: { album: AlbumDto; onClose: () => void }) {
  const deleteAlbum = useDeleteAlbum();
  const navigate = useNavigate();

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

  const confirm = () => {
    if (deleteAlbum.isPending) return;
    deleteAlbum.mutate(album.id, {
      // Navigate away immediately - the page under this dialog derives from
      // the album list, and staying while invalidation refetches it would
      // flash "could not load that album" for an album that no longer exists.
      onSuccess: () => void navigate({ to: '/albums' }),
    });
  };

  return (
    <div className="fixed inset-0 z-50 grid place-items-center p-4">
      <button
        type="button"
        aria-label="Close delete confirmation"
        className="absolute inset-0 bg-black/60"
        onClick={onClose}
      />

      <div
        role="dialog"
        aria-modal="true"
        aria-label={`Delete album ${album.albumName}`}
        className="relative w-full max-w-sm rounded-lg border border-line bg-overlay p-4 shadow-2xl"
      >
        <h2 className="text-base font-semibold text-ink">Delete “{album.albumName}”?</h2>

        <p className="mt-2 text-sm text-ink-muted">
          This removes the album and its{' '}
          <span className="tabular-nums">{album.assetCount.toLocaleString()}</span>{' '}
          {album.assetCount === 1 ? 'item' : 'items'} from your albums list. The files themselves
          are never touched - they stay on disk, in your library and in any other album.
        </p>

        {deleteAlbum.isError && (
          <p className="mt-2 text-xs text-danger">
            Could not delete the album: {apiErrorMessage(deleteAlbum.error)}
          </p>
        )}

        <div className="mt-4 flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            className="rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted transition-colors hover:bg-raised hover:text-ink"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={confirm}
            disabled={deleteAlbum.isPending}
            className="rounded-md bg-danger px-3 py-1.5 text-sm font-medium text-ink-inverse transition-colors hover:opacity-90 disabled:opacity-50"
          >
            {deleteAlbum.isPending ? 'Deleting…' : 'Delete album'}
          </button>
        </div>
      </div>
    </div>
  );
}
