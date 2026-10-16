import { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate } from '@tanstack/react-router';
import { assetOriginalUrl, assetThumbnailUrl, assetVideoUrl } from '@/shared/api/client';
import type { TimelineAsset } from '@/shared/api/timeline';
import { Thumbhash } from '@/shared/ui/Thumbhash';
import { ConfirmDialog } from '@/features/selection/ConfirmDialog';
import { useBulkFavorite, useBulkTrash } from '@/features/selection/api';
import { useRestoreAssets } from '@/features/trash/api';
import { apiErrorMessage } from '@/shared/api/tessera';
import { INSECURE_CONTEXT_HINT, canCopyImages, copyImageToClipboard } from './copyImage';
import { InfoPanel } from './InfoPanel';

/**
 * Full-screen viewer.
 *
 * Two details matter more than they look:
 *
 * 1. LAYERED LOADING. We paint the thumbhash, then the small thumbnail we
 *    already have cached from the grid, then the original - often several
 *    megabytes off a disk. Without the intermediate thumbnail the viewer
 *    would show a blur for a noticeably long second.
 *
 * 2. KEYBOARD FIRST. Arrow keys page, Escape closes, F favourites, Delete
 *    trashes, I toggles the info panel, M finds more like this. A viewer you
 *    have to drive with a mouse is exhausting.
 *
 * The actions reuse the SELECTION feature's mutations with a single id - a
 * bulk endpoint with one element is the same endpoint.
 *
 * Opened from the Trash page (`mode="trash"`) the one action that fits is
 * Restore: favouriting or finding "more like this" from a hidden picture is
 * not coherent, and moving it to the trash again even less so.
 */

export interface AssetViewerProps {
  assets: TimelineAsset[];
  index: number;
  onIndexChange: (index: number) => void;
  onClose: () => void;
  /** `trash`: the picture is in the trash; offer Restore instead of Trash. */
  mode?: 'library' | 'trash';
}

const INFO_PANEL_KEY = 'tessera.viewer.info';

export function AssetViewer({ assets, index, onIndexChange, onClose, mode = 'library' }: AssetViewerProps) {
  const asset = assets[index];
  const inTrash = mode === 'trash';
  const navigate = useNavigate();
  // Whether the original has arrived. The thumbnail stays up until the
  // original has decoded (and stays for good if the browser cannot decode it).
  const [fullLoaded, setFullLoaded] = useState(false);
  const [confirmingTrash, setConfirmingTrash] = useState(false);

  // The info panel is OPEN by default - the file's name, where it lives and
  // the album it belongs to are what one looks at - and it stays open across
  // pictures. Closing it is remembered for the session.
  const [showInfo, setShowInfoState] = useState(() => {
    try {
      return sessionStorage.getItem(INFO_PANEL_KEY) !== 'closed';
    } catch {
      return true;
    }
  });
  const setShowInfo = (open: boolean) => {
    setShowInfoState(open);
    try {
      sessionStorage.setItem(INFO_PANEL_KEY, open ? 'open' : 'closed');
    } catch {
      /* private window: the default wins each time */
    }
  };

  /*
   * `assets` is a SNAPSHOT the grid handed us when the viewer opened, so a
   * favourite toggled in here would not repaint until the grid refetches. The
   * overrides map is the viewer's own record of what it changed, consulted
   * before the snapshot - honest optimism, scoped to this viewer session.
   */
  const [favoriteOverrides, setFavoriteOverrides] = useState<Record<string, boolean>>({});

  /*
   * Pictures trashed (or, in the trash, restored) from this viewer. The
   * snapshot still holds them, so they are skipped rather than spliced out:
   * the parent owns the index into the snapshot. Without this the counter
   * kept counting a trashed picture and one step back showed it again, ready
   * to be trashed a second time (Windows UI test, 2026-10-16).
   */
  const [removed, setRemoved] = useState<ReadonlySet<string>>(() => new Set());

  const favorite = useBulkFavorite();
  const trash = useBulkTrash();
  const restore = useRestoreAssets();

  const isFavorite = asset ? (favoriteOverrides[asset.id] ?? asset.isFavorite) : false;

  const nextLive = useCallback(
    (from: number, step: 1 | -1, gone: ReadonlySet<string> = removed) => {
      for (let i = from + step; i >= 0 && i < assets.length; i += step) {
        const candidate = assets[i];
        if (candidate && !gone.has(candidate.id)) return i;
      }
      return null;
    },
    [assets, removed],
  );

  const { position, liveCount } = useMemo(() => {
    let live = 0;
    let at = 0;
    assets.forEach((candidate, i) => {
      if (removed.has(candidate.id)) return;
      live += 1;
      if (i <= index) at = live;
    });
    return { position: at, liveCount: live };
  }, [assets, removed, index]);

  const go = useCallback(
    (delta: 1 | -1) => {
      const next = nextLive(index, delta);
      if (next !== null) onIndexChange(next);
    },
    [index, nextLive, onIndexChange],
  );

  /** The current picture left this view: show the next one, or close. */
  const leaveCurrent = useCallback(() => {
    if (!asset) return;
    const gone = new Set(removed).add(asset.id);
    setRemoved(gone);
    const next = nextLive(index, 1, gone) ?? nextLive(index, -1, gone);
    if (next === null) onClose();
    else onIndexChange(next);
  }, [asset, removed, nextLive, index, onIndexChange, onClose]);

  const toggleFavorite = useCallback(() => {
    if (!asset) return;
    const next = !isFavorite;
    setFavoriteOverrides((current) => ({ ...current, [asset.id]: next }));
    favorite.mutate(
      { ids: [asset.id], isFavorite: next },
      {
        // Roll the optimistic flag back if the core said no, so the star
        // never lies about saved state.
        onError: () => setFavoriteOverrides((current) => ({ ...current, [asset.id]: !next })),
      },
    );
  }, [asset, isFavorite, favorite]);

  /**
   * "More like this": the search page with this picture's vector as the
   * query. The viewer closes first so the results are not hidden behind it.
   */
  const moreLikeThis = useCallback(() => {
    if (!asset) return;
    onClose();
    void navigate({ to: '/search', search: { q: '', albumId: undefined, similar: asset.id } });
  }, [asset, onClose, navigate]);

  const confirmTrash = useCallback(() => {
    if (!asset) return;
    trash.mutate([asset.id], {
      onSuccess: () => {
        setConfirmingTrash(false);
        leaveCurrent();
      },
    });
  }, [asset, trash, leaveCurrent]);

  const restoreCurrent = useCallback(() => {
    if (!asset) return;
    restore.mutate([asset.id], { onSuccess: leaveCurrent });
  }, [asset, restore, leaveCurrent]);

  useEffect(() => {
    setFullLoaded(false);
  }, [asset?.id]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      // While the trash confirm is up, IT owns the keyboard.
      if (confirmingTrash) return;

      switch (event.key) {
        case 'Escape':
          onClose();
          break;
        case 'ArrowRight':
          go(1);
          break;
        case 'ArrowLeft':
          go(-1);
          break;
        case 'f':
        case 'F':
          if (!inTrash) toggleFavorite();
          break;
        case 'Delete':
          if (!inTrash) setConfirmingTrash(true);
          break;
        case 'i':
        case 'I':
          setShowInfo(!showInfo);
          break;
        case 'm':
        case 'M':
          if (!inTrash) moreLikeThis();
          break;
        default:
          break;
      }
    };

    window.addEventListener('keydown', onKey);
    // Stop the page behind the overlay scrolling while it is open.
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    return () => {
      window.removeEventListener('keydown', onKey);
      document.body.style.overflow = previousOverflow;
    };
  }, [go, onClose, toggleFavorite, moreLikeThis, confirmingTrash, showInfo, inTrash]);

  // Preload the neighbours so paging feels instant rather than staged.
  useEffect(() => {
    for (const offset of [1, -1]) {
      const neighbour = assets[index + offset];
      if (neighbour?.isImage) {
        const image = new Image();
        image.src = assetThumbnailUrl(neighbour.id);
      }
    }
  }, [assets, index]);

  if (!asset) return null;

  return (
    <div
      className="fixed inset-0 z-50 flex flex-col bg-black/95 backdrop-blur-sm"
      role="dialog"
      aria-modal="true"
      aria-label="Picture viewer"
    >
      <header className="flex h-14 shrink-0 items-center gap-3 px-4 text-white/80">
        <button
          type="button"
          onClick={onClose}
          className="rounded-md p-2 hover:bg-white/10 hover:text-white"
          aria-label="Close viewer"
        >
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
            <path d="M18 6 6 18M6 6l12 12" />
          </svg>
        </button>

        <span className="text-sm tabular-nums">
          {position.toLocaleString()} / {liveCount.toLocaleString()}
        </span>

        <span className="ml-auto text-sm text-white/60">{formatDate(asset.fileCreatedAt)}</span>

        {!inTrash && (
        <button
          type="button"
          onClick={toggleFavorite}
          className={`rounded-md p-2 hover:bg-white/10 ${isFavorite ? 'text-accent' : 'hover:text-white'}`}
          aria-label={isFavorite ? 'Remove from favourites' : 'Add to favourites'}
          aria-pressed={isFavorite}
          title={`${isFavorite ? 'Unfavourite' : 'Favourite'} (F)`}
        >
          <svg width="20" height="20" viewBox="0 0 24 24" fill={isFavorite ? 'currentColor' : 'none'} stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round">
            <path d="m12 3 2.7 5.8 6.3.7-4.7 4.3 1.3 6.2L12 16.9 6.4 20l1.3-6.2L3 9.5l6.3-.7Z" />
          </svg>
        </button>
        )}

        {asset.isImage && !inTrash && (
          <button
            type="button"
            onClick={moreLikeThis}
            className="inline-flex items-center gap-1.5 rounded-md px-2.5 py-2 text-sm hover:bg-white/10 hover:text-white"
            aria-label="Find pictures like this one"
            title="More like this (M)"
          >
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
              <rect x="3" y="3" width="8" height="8" rx="1.5" />
              <rect x="13" y="3" width="8" height="8" rx="1.5" />
              <rect x="3" y="13" width="8" height="8" rx="1.5" />
              <path d="M17 14v6M14 17h6" />
            </svg>
            More like this
          </button>
        )}

        {asset.isImage && <CopyImageButton assetId={asset.id} />}

        <a
          href={assetOriginalUrl(asset.id)}
          download
          className="rounded-md p-2 hover:bg-white/10 hover:text-white"
          aria-label="Save a copy of the original"
          title="Save a copy"
        >
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
            <path d="M12 3v12m0 0 4-4m-4 4-4-4M4 19h16" />
          </svg>
        </a>

        <button
          type="button"
          onClick={() => setShowInfo(!showInfo)}
          aria-pressed={showInfo}
          className={`rounded-md p-2 hover:bg-white/10 ${showInfo ? 'text-accent' : 'hover:text-white'}`}
          aria-label={showInfo ? 'Hide picture information' : 'Show picture information'}
          title="Info (I)"
        >
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round">
            <circle cx="12" cy="12" r="9" />
            <path d="M12 11v5" />
            <circle cx="12" cy="7.75" r="0.9" fill="currentColor" stroke="none" />
          </svg>
        </button>

        {inTrash ? (
          <button
            type="button"
            onClick={restoreCurrent}
            disabled={restore.isPending}
            className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-ink-inverse transition-colors hover:bg-accent-hover disabled:cursor-wait disabled:opacity-60"
          >
            {restore.isPending ? 'Restoring…' : 'Restore'}
          </button>
        ) : (
          <button
            type="button"
            onClick={() => setConfirmingTrash(true)}
            className="rounded-md p-2 hover:bg-white/10 hover:text-danger"
            aria-label="Move to trash"
            title="Move to trash (Delete)"
          >
            <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
              <path d="M4 7h16M10 11v6m4-6v6M6 7l1 13h10l1-13M9 7V4h6v3" />
            </svg>
          </button>
        )}
      </header>

      {restore.isError && (
        <p role="alert" className="bg-danger/20 px-4 py-2 text-sm text-danger">
          Could not restore this picture: {apiErrorMessage(restore.error)}
        </p>
      )}

      {/* A ROW, so the panel sits beside the picture rather than over it. The
          inner div keeps its own `relative`, which the nav buttons position
          against - they must track the IMAGE, not the image-plus-sidebar. */}
      <div className="flex min-h-0 flex-1">
        <div className="relative min-h-0 flex-1">
          {asset.isImage ? (
            <>
              {/* Layer 1: the blur we already have, zero network cost. */}
              {!fullLoaded && (
                <div className="absolute inset-0 flex items-center justify-center">
                  <Thumbhash hash={asset.thumbhash} className="h-full w-full opacity-60 blur-xl" />
                </div>
              )}

              {/* Layer 2: the grid thumbnail, already cached by the browser. */}
              {!fullLoaded && (
                <img
                  src={assetThumbnailUrl(asset.id)}
                  alt=""
                  aria-hidden
                  className="absolute inset-0 h-full w-full object-contain"
                />
              )}

              {/* Layer 3: the original. If the browser cannot decode it, the
                  thumbnail stays. */}
              <img
                key={asset.id}
                src={assetOriginalUrl(asset.id)}
                alt=""
                onLoad={() => setFullLoaded(true)}
                className="absolute inset-0 h-full w-full object-contain transition-opacity duration-300"
                style={{ opacity: fullLoaded ? 1 : 0 }}
              />
            </>
          ) : (
            <video
              key={asset.id}
              src={assetVideoUrl(asset.id)}
              poster={assetThumbnailUrl(asset.id)}
              controls
              autoPlay
              className="absolute inset-0 h-full w-full object-contain"
            />
          )}

          <NavButton side="left" onClick={() => go(-1)} disabled={nextLive(index, -1) === null} />
          <NavButton side="right" onClick={() => go(1)} disabled={nextLive(index, 1) === null} />
        </div>

        {showInfo && (
          <InfoPanel
            // Keyed by asset so switching pictures remounts the panel rather
            // than showing the previous picture's details while the new
            // request is in flight.
            key={asset.id}
            assetId={asset.id}
            onNavigate={onClose}
          />
        )}
      </div>

      {confirmingTrash && (
        <ConfirmDialog
          title="Move to trash?"
          confirmLabel="Move to trash"
          danger
          busy={trash.isPending}
          onConfirm={confirmTrash}
          onCancel={() => setConfirmingTrash(false)}
        >
          This picture will be hidden in the trash. The file on disk is not touched - you can
          restore it from the Trash page.
        </ConfirmDialog>
      )}
    </div>
  );
}

function NavButton({
  side,
  onClick,
  disabled,
}: {
  side: 'left' | 'right';
  onClick: () => void;
  disabled: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={side === 'left' ? 'Previous' : 'Next'}
      className={`absolute top-1/2 -translate-y-1/2 rounded-full bg-black/40 p-3 text-white/80 transition hover:bg-black/60 hover:text-white disabled:pointer-events-none disabled:opacity-0 ${
        side === 'left' ? 'left-4' : 'right-4'
      }`}
    >
      <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
        <path d={side === 'left' ? 'm15 6-6 6 6 6' : 'm9 6 6 6-6 6'} />
      </svg>
    </button>
  );
}

function formatDate(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
}

/**
 * Copy the picture to the clipboard, for pasting into whatever one is
 * drawing or writing in. The button reports its own outcome for a moment
 * instead of raising a toast: copying is a small, immediate act, and the
 * feedback belongs where the click happened.
 */
function CopyImageButton({ assetId }: { assetId: string }) {
  const [state, setState] = useState<'idle' | 'copying' | 'copied' | 'failed'>('idle');
  const [message, setMessage] = useState<string | null>(null);
  const supported = canCopyImages();

  useEffect(() => {
    if (state !== 'copied' && state !== 'failed') return;
    const timer = setTimeout(() => {
      setState('idle');
      setMessage(null);
    }, 2400);
    return () => clearTimeout(timer);
  }, [state]);

  if (!supported) {
    return (
      <button
        type="button"
        disabled
        aria-label="Copy image (needs a secure context)"
        title={INSECURE_CONTEXT_HINT}
        className="cursor-not-allowed rounded-md p-2 opacity-40"
      >
        <CopyIcon />
      </button>
    );
  }

  return (
    <div className="relative">
      <button
        type="button"
        disabled={state === 'copying'}
        onClick={async () => {
          setState('copying');
          try {
            await copyImageToClipboard(assetOriginalUrl(assetId));
            setState('copied');
          } catch (error) {
            setState('failed');
            setMessage((error as Error).message);
          }
        }}
        aria-label="Copy image to clipboard"
        title="Copy image to clipboard"
        className={`rounded-md p-2 hover:bg-white/10 hover:text-white ${
          state === 'copied' ? 'text-success' : state === 'failed' ? 'text-danger' : ''
        }`}
      >
        {state === 'copied' ? <TickIcon /> : <CopyIcon />}
      </button>

      {(state === 'copied' || state === 'failed') && (
        <span
          role="status"
          className={`absolute right-0 top-full z-10 mt-1 w-max max-w-xs rounded-md px-2 py-1 text-xs shadow-lg ${
            state === 'copied' ? 'bg-black/80 text-white' : 'bg-danger text-white'
          }`}
        >
          {state === 'copied' ? 'Copied' : (message ?? 'Copy failed')}
        </span>
      )}
    </div>
  );
}

function CopyIcon() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
      <rect x="9" y="9" width="11" height="11" rx="2" />
      <path d="M5 15V5a2 2 0 0 1 2-2h10" />
    </svg>
  );
}

function TickIcon() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" aria-hidden>
      <path d="m5 13 4 4L19 7" />
    </svg>
  );
}
