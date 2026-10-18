import { useEffect, useState } from 'react';
import { useSelection } from './SelectionContext';
import { AlbumPicker } from './AlbumPicker';
import { ConfirmDialog } from './ConfirmDialog';
import { useBulkAddToAlbum, useBulkFavorite, useBulkRemoveFromAlbum, useBulkTrash } from './api';
import type { BulkIdResponseDto } from '@/shared/api/types';
import { apiErrorMessage } from '@/shared/api/tessera';
import { useAlbums } from '@/shared/api/queries';

/**
 * The floating action bar for a live selection.
 *
 * Mounted by any page that wraps its grid in `SelectionProvider`; it renders
 * nothing while the selection is empty, so pages can mount it unconditionally.
 *
 * Which dialog is open is ONE state field, not several booleans - two dialogs
 * at once is not a state this UI can be in, and modelling it as a union makes
 * that unrepresentable instead of merely unlikely.
 *
 * Post-action behaviour is deliberate: favourite/unfavourite and add-to-album
 * KEEP the selection (people chain those - favourite fifty, then album them);
 * trash CLEARS it, because the selected things no longer exist here.
 */
type OpenPanel = 'none' | 'albums' | 'trash-confirm';

/** @param albumId - on an album's page: offers "Remove from album". */
export function SelectionBar({ albumId }: { albumId?: string } = {}) {
  const selection = useSelection();
  const [panel, setPanel] = useState<OpenPanel>('none');

  const favorite = useBulkFavorite();
  const addToAlbum = useBulkAddToAlbum();
  const removeFromAlbum = useBulkRemoveFromAlbum();
  const trash = useBulkTrash();
  const albums = useAlbums();

  // "Added 4 to Lakes": the picker closes and the selection stays (people
  // chain actions), so without a line saying so nothing showed that the add
  // happened (Windows UI test, 2026-10-16). Gone after a few seconds.
  const [added, setAdded] = useState<string | null>(null);
  useEffect(() => {
    if (!added) return;
    const timer = setTimeout(() => setAdded(null), 4000);
    return () => clearTimeout(timer);
  }, [added]);

  if (!selection.active) return null;

  const ids = [...selection.selected];
  const error = favorite.error ?? addToAlbum.error ?? removeFromAlbum.error ?? trash.error ?? null;

  return (
    <div className="pointer-events-none fixed inset-x-0 bottom-6 z-40 flex flex-col items-center gap-2 px-4">
      {error != null && (
        <p className="pointer-events-auto rounded-md border border-danger/40 bg-overlay px-3 py-1.5 text-sm text-danger shadow-lg">
          {apiErrorMessage(error)}
        </p>
      )}

      {panel === 'albums' && (
        <div className="pointer-events-auto">
          <AlbumPicker
            disabled={addToAlbum.isPending}
            onPick={(albumId) =>
              addToAlbum.mutate(
                { albumId, ids },
                {
                  onSuccess: (results) => {
                    setPanel('none');
                    const name = albums.data?.find((album) => album.id === albumId)?.albumName ?? 'the album';
                    setAdded(addedMessage(results, ids.length, name));
                  },
                },
              )
            }
          />
        </div>
      )}

      {added && (
        <p
          role="status"
          className="pointer-events-auto rounded-md border border-line bg-overlay px-3 py-1.5 text-sm text-ink shadow-lg"
        >
          {added}
        </p>
      )}

      <div className="pointer-events-auto flex items-center gap-1 rounded-xl border border-line bg-overlay px-2 py-1.5 shadow-xl">
        <span className="px-2 text-sm font-medium tabular-nums text-ink">
          {selection.count.toLocaleString()} selected
        </span>

        <Divider />

        <BarButton
          label="Favourite"
          busy={favorite.isPending}
          onClick={() => favorite.mutate({ ids, isFavorite: true })}
          icon={<StarIcon filled />}
        />
        <BarButton
          label="Unfavourite"
          busy={favorite.isPending}
          onClick={() => favorite.mutate({ ids, isFavorite: false })}
          icon={<StarIcon />}
        />
        <BarButton
          label="Add to album"
          busy={addToAlbum.isPending}
          onClick={() => setPanel((current) => (current === 'albums' ? 'none' : 'albums'))}
          icon={<AlbumIcon />}
        />
        {albumId && (
          <BarButton
            label="Remove from album"
            busy={removeFromAlbum.isPending}
            onClick={() =>
              removeFromAlbum.mutate(
                { albumId, ids },
                {
                  onSuccess: (results) => {
                    const removed = results ? results.filter((result) => result.success).length : ids.length;
                    setAdded(`Removed ${removed.toLocaleString()} from this album. They stay in your library.`);
                    selection.clear();
                  },
                },
              )
            }
            icon={<RemoveIcon />}
          />
        )}
        <BarButton
          label="Move to trash"
          danger
          busy={trash.isPending}
          onClick={() => setPanel('trash-confirm')}
          icon={<TrashIcon />}
        />

        <Divider />

        <BarButton label="Clear selection" onClick={selection.clear} icon={<CloseIcon />} />
      </div>

      {panel === 'trash-confirm' && (
        <ConfirmDialog
          title="Move to trash?"
          confirmLabel="Move to trash"
          danger
          busy={trash.isPending}
          onConfirm={() =>
            trash.mutate(ids, {
              onSuccess: () => {
                setPanel('none');
                selection.clear();
              },
            })
          }
          onCancel={() => setPanel('none')}
        >
          {ids.length.toLocaleString()} item{ids.length === 1 ? '' : 's'} will be hidden in the
          trash. The files on disk are not touched - you can restore them from the Trash page.
        </ConfirmDialog>
      )}
    </div>
  );
}

/** What one add did, in counts: the core answers per id, and a picture already in the album is not added twice. */
export function addedMessage(results: BulkIdResponseDto[] | undefined, asked: number, album: string): string {
  const added = results ? results.filter((result) => result.success).length : asked;
  const already = asked - added;
  return `Added ${added.toLocaleString()} to ${album}` + (already > 0 ? ` · ${already.toLocaleString()} already there` : '');
}

function BarButton({
  label,
  icon,
  onClick,
  busy,
  danger,
}: {
  label: string;
  icon: React.ReactNode;
  onClick: () => void;
  busy?: boolean;
  danger?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={busy}
      title={label}
      aria-label={label}
      className={`rounded-lg p-2 transition-colors disabled:cursor-wait disabled:opacity-50 ${
        danger ? 'text-ink-muted hover:bg-raised hover:text-danger' : 'text-ink-muted hover:bg-raised hover:text-ink'
      }`}
    >
      {icon}
    </button>
  );
}

function Divider() {
  return <span aria-hidden className="mx-0.5 h-5 w-px bg-line" />;
}

function StarIcon({ filled }: { filled?: boolean }) {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill={filled ? 'currentColor' : 'none'} stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round" aria-hidden>
      <path d="m12 3 2.7 5.8 6.3.7-4.7 4.3 1.3 6.2L12 16.9 6.4 20l1.3-6.2L3 9.5l6.3-.7Z" />
    </svg>
  );
}

function AlbumIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <rect x="3" y="5" width="14" height="14" rx="2" />
      <path d="M21 8v9a3 3 0 0 1-3 3h-9M7 15l3-3 4 4M13 11l2-2 2 2" />
    </svg>
  );
}

function RemoveIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <rect x="3" y="5" width="14" height="14" rx="2" />
      <path d="M7 12h6M19 4l-3 3m0-3 3 3" />
    </svg>
  );
}

function TrashIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <path d="M4 7h16M10 11v6m4-6v6M6 7l1 13h10l1-13M9 7V4h6v3" />
    </svg>
  );
}

function CloseIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <path d="M18 6 6 18M6 6l12 12" />
    </svg>
  );
}
