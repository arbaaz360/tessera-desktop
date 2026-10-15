import { useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from '@tanstack/react-router';
import { IndexAlbum } from './IndexAlbum';
import { useAlbum } from '@/shared/api/queries';
import { TimelineGrid } from '@/features/timeline/TimelineGrid';
import { AssetViewer } from '@/features/viewer/AssetViewer';
import { MediaTypeFilter, type MediaTypeValue } from '@/shared/ui/MediaTypeFilter';
import { CoverPickerModal } from '@/features/covers/CoverPickerModal';
import { useCoverPickerHealth } from '@/features/covers/api';
import { SelectionProvider } from '@/features/selection/SelectionContext';
import { SelectionBar } from '@/features/selection/SelectionBar';
import type { TimelineAsset } from '@/shared/api/timeline';
import type { AlbumResponseDto } from '@immich/sdk';
import { useDeleteAlbum, useRenameAlbum } from './api';

/**
 * A single album.
 *
 * Reuses `TimelineGrid` rather than reimplementing a grid — an album is just a
 * timeline with `albumId` in the query, so it inherits bucketing,
 * virtualisation and lazy loading for free. Some albums here hold 183,075
 * assets, which rules out any approach that renders them all.
 *
 * The album's metadata comes from the cached album LIST via `useAlbum`, not an
 * individual fetch — v3's AlbumResponseDto carries assetCount and no assets, which
 * for the largest album is a response nobody wants. The grid fetches assets
 * itself, in month-sized pieces.
 *
 * Management lives here too: rename inline on the title, delete behind a
 * confirm dialog, and the cover picker. The rename rides the optimistic
 * mutation in `api.ts`, so the title updates the instant Enter is pressed and
 * only snaps back if the server refuses.
 */
export function AlbumDetailPage() {
  const { albumId } = useParams({ from: '/authed/albums/$albumId' });
  const navigate = useNavigate();

  const { data: album, isPending, isError } = useAlbum(albumId);
  const [mediaType, setMediaType] = useState<MediaTypeValue>('ALL');
  const [viewer, setViewer] = useState<{ assets: TimelineAsset[]; index: number } | null>(null);
  const [smartTerm, setSmartTerm] = useState('');
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [pickingCover, setPickingCover] = useState(false);

  // "Is the sidecar up" — one cached probe (see covers/api.ts). Down means the
  // button renders disabled with an explanation, and nothing else on this page
  // is any different.
  const coverHealth = useCoverPickerHealth();
  const coverPickerDown = coverHealth.data === false;

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

  return (
    <div className="flex h-full flex-col">
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
            {/* Search scoped to this album. Sends the user to the search page
                with albumId pinned, rather than duplicating result rendering. */}
            <form
              onSubmit={(event) => {
                event.preventDefault();
                if (smartTerm.trim()) {
                  void navigate({ to: '/search', search: { q: smartTerm.trim(), albumId } });
                }
              }}
            >
              <input
                type="search"
                value={smartTerm}
                onChange={(event) => setSmartTerm(event.target.value)}
                placeholder="Search in this album"
                aria-label="Search within this album"
                className="w-52 rounded-md border border-line bg-surface px-3 py-1.5 text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none"
              />
            </form>

            <MediaTypeFilter value={mediaType} onChange={setMediaType} />

            <IndexAlbum album={album} />

            <button
              type="button"
              onClick={() => setPickingCover(true)}
              disabled={coverPickerDown}
              title={coverPickerDown ? 'The cover picker service is not running' : 'Pick a cover from ranked candidates'}
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
          view only, so navigating away ends the selection, and the grid grows
          its checkboxes because a provider is present. */}
      <SelectionProvider>
        <div className="min-h-0 flex-1">
          <TimelineGrid
            query={query}
            onSelect={(asset, all) => setViewer({ assets: all, index: all.indexOf(asset) })}
            emptyMessage={
              mediaType === 'ALL'
                ? 'This album is empty. Select photos in the library and add them, or copy a folder into it.'
                : `No ${mediaType === 'IMAGE' ? 'photos' : 'videos'} in this album.`
            }
          />
        </div>

        <SelectionBar />
      </SelectionProvider>

      {viewer && (
        <AssetViewer
          assets={viewer.assets}
          index={viewer.index}
          onIndexChange={(index) => setViewer((current) => (current ? { ...current, index } : null))}
          onClose={() => setViewer(null)}
        />
      )}

      {pickingCover && (
        <CoverPickerModal
          albumId={album.id}
          albumName={album.albumName}
          onClose={() => setPickingCover(false)}
        />
      )}

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
 * Escape both just blur the input — Enter after setting nothing, Escape after
 * setting a ref that tells the blur handler to discard. Funnelling everything
 * through one handler avoids the classic double-commit bug where Enter
 * mutates, the state change unmounts the input, and the resulting blur
 * mutates again.
 *
 * The mutation itself is optimistic (see useRenameAlbum in ./api.ts): the
 * title shows the new name immediately, and reverts — with the error shown
 * below — only if the server rejects it.
 */
function AlbumTitle({ album }: { album: AlbumResponseDto }) {
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
        <p className="text-xs text-danger">
          Rename failed: {(rename.error as Error).message}
        </p>
      )}
    </>
  );
}

/**
 * Delete confirmation.
 *
 * The dialog names the album and its size because "Delete" on a generic
 * confirm is how the wrong album dies — the user should be confirming THIS
 * album, not a yes/no reflex. It also says out loud what deleting an album
 * does NOT do: an album is a grouping, and removing it never touches the
 * photos. That single sentence is the difference between a safe click and a
 * scary one.
 *
 * Escape handling and the scroll lock mirror CoverPickerModal, so every
 * overlay in the app behaves the same way.
 */
function DeleteAlbumDialog({ album, onClose }: { album: AlbumResponseDto; onClose: () => void }) {
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
      // Navigate away immediately — the page under this dialog derives from
      // the album list, and staying while invalidation refetches it would
      // flash "could not load that album" for an album that simply no longer
      // exists.
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
        <h2 className="text-base font-semibold text-ink">
          Delete “{album.albumName}”?
        </h2>

        <p className="mt-2 text-sm text-ink-muted">
          This removes the album and its{' '}
          <span className="tabular-nums">{album.assetCount.toLocaleString()}</span>{' '}
          {album.assetCount === 1 ? 'item' : 'items'} from your albums list. The photos and videos
          themselves are never deleted — they stay in your library and in any other album.
        </p>

        {deleteAlbum.isError && (
          <p className="mt-2 text-xs text-danger">
            Could not delete the album: {(deleteAlbum.error as Error).message}
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
