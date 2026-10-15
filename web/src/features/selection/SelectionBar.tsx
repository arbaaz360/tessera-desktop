import { useState } from 'react';
import { useSelection } from './SelectionContext';
import { AlbumPicker } from './AlbumPicker';
import { ConfirmDialog } from './ConfirmDialog';
import {
  DOWNLOAD_WARN_THRESHOLD,
  useBulkAddToAlbum,
  useBulkDownload,
  useBulkFavorite,
  useBulkTrash,
} from './api';
// Reused rather than re-implemented: admin/api.ts already knows how to read
// an SDK HttpError, and the knowledge should exist once.
import { apiErrorMessage } from '@/features/admin/api';

/**
 * The floating action bar for a live selection.
 *
 * Mounted by any page that wraps its grid in `SelectionProvider`; it renders
 * nothing while the selection is empty, so pages can mount it unconditionally.
 *
 * Which dialog is open is ONE state field, not four booleans — two dialogs at
 * once is not a state this UI can be in, and modelling it as a union makes
 * that unrepresentable instead of merely unlikely.
 *
 * Post-action behaviour is deliberate: favorite/unfavorite and add-to-album
 * KEEP the selection (people chain those — favorite fifty, then album them);
 * trash CLEARS it, because the selected things no longer exist here.
 */
type OpenPanel = 'none' | 'albums' | 'download-warning' | 'trash-confirm';

export function SelectionBar() {
  const selection = useSelection();
  const [panel, setPanel] = useState<OpenPanel>('none');

  const favorite = useBulkFavorite();
  const addToAlbum = useBulkAddToAlbum();
  const download = useBulkDownload();
  const trash = useBulkTrash();

  if (!selection.active) return null;

  const ids = [...selection.selected];
  const error =
    favorite.error ?? addToAlbum.error ?? download.error ?? trash.error ?? null;

  const runDownload = () => {
    setPanel('none');
    download.mutate(ids);
  };

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
              addToAlbum.mutate({ albumId, ids }, { onSuccess: () => setPanel('none') })
            }
          />
        </div>
      )}

      <div className="pointer-events-auto flex items-center gap-1 rounded-xl border border-line bg-overlay px-2 py-1.5 shadow-xl">
        <span className="px-2 text-sm font-medium tabular-nums text-ink">
          {selection.count.toLocaleString()} selected
        </span>

        <Divider />

        <BarButton
          label="Favorite"
          busy={favorite.isPending}
          onClick={() => favorite.mutate({ ids, isFavorite: true })}
          icon={<StarIcon filled />}
        />
        <BarButton
          label="Unfavorite"
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
        <BarButton
          label="Download"
          busy={download.isPending}
          onClick={() =>
            // Past the threshold the server will be zipping originals for a
            // while and the archive may run to gigabytes — worth a pause.
            ids.length > DOWNLOAD_WARN_THRESHOLD ? setPanel('download-warning') : runDownload()
          }
          icon={<DownloadIcon />}
        />
        <BarButton
          label="Delete"
          danger
          busy={trash.isPending}
          onClick={() => setPanel('trash-confirm')}
          icon={<TrashIcon />}
        />

        <Divider />

        <BarButton label="Clear selection" onClick={selection.clear} icon={<CloseIcon />} />
      </div>

      {panel === 'download-warning' && (
        <ConfirmDialog
          title="Large download"
          confirmLabel={`Download ${ids.length.toLocaleString()} items`}
          busy={download.isPending}
          onConfirm={runDownload}
          onCancel={() => setPanel('none')}
        >
          You are about to archive {ids.length.toLocaleString()} original files. The server
          zips them before the download starts, so this can take a while and produce a very
          large file.
        </ConfirmDialog>
      )}

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
          {ids.length.toLocaleString()} item{ids.length === 1 ? '' : 's'} will move to the
          trash. Nothing is deleted permanently — you can restore them from the Trash page.
        </ConfirmDialog>
      )}
    </div>
  );
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

/* Icons are inline SVG, matching the rest of the app: no icon dependency,
   and `currentColor` inherits each button's hover state for free. */

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

function DownloadIcon() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <path d="M12 3v12m0 0 4-4m-4 4-4-4M4 19h16" />
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
