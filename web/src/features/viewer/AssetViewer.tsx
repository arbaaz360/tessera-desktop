import { useCallback, useEffect, useState } from 'react';
import { assetOriginalUrl, assetThumbnailUrl, assetVideoUrl } from '@/shared/api/client';
import { Link } from '@tanstack/react-router';
import type { TimelineAsset } from '@/shared/api/timeline';
import { Thumbhash } from '@/shared/ui/Thumbhash';
import { NowPlayingPanel } from '@/features/music/NowPlayingPanel';
import { ConfirmDialog } from '@/features/selection/ConfirmDialog';
import { useBulkFavorite, useBulkTrash } from '@/features/selection/api';
import { INSECURE_CONTEXT_HINT, canCopyImages, copyImageToClipboard } from './copyImage';
import { InfoPanel } from './InfoPanel';

/**
 * Full-screen asset viewer.
 *
 * Two details matter more than they look:
 *
 * 1. LAYERED LOADING. We paint the thumbhash, then the small thumbnail we
 *    already have cached from the grid, then the full image. In this deployment
 *    preview generation is disabled, so the "full image" is the ORIGINAL — often
 *    several megabytes off a hard disk. Without the intermediate thumbnail the
 *    viewer would show a blur for a noticeably long second.
 *
 * 2. KEYBOARD FIRST. Arrow keys page, Escape closes, F favorites, Delete
 *    trashes. A photo viewer you have to drive with a mouse is exhausting;
 *    this is the difference between reviewing fifty photos and giving up
 *    after ten.
 *
 * The actions reuse the SELECTION feature's mutations with a single id — a
 * bulk endpoint with one element is the same endpoint, and duplicating the
 * mutation just to drop the array would mean two invalidation policies to
 * keep in sync.
 */


export interface AssetViewerProps {
  assets: TimelineAsset[];
  index: number;
  onIndexChange: (index: number) => void;
  onClose: () => void;
}

const INFO_PANEL_KEY = 'photos-web.viewer.info';

export function AssetViewer({ assets, index, onIndexChange, onClose }: AssetViewerProps) {
  const asset = assets[index];
  // Whether the original has arrived. Two sizes exist in this library, the
  // grid thumbnail and the original; the thumbnail stays up until the
  // original has decoded (and stays for good if the browser cannot decode
  // it - HEIC, RAW).
  const [fullLoaded, setFullLoaded] = useState(false);
  const [confirmingTrash, setConfirmingTrash] = useState(false);

  // The info panel is OPEN by default (the owner's rule: the file's name,
  // date and the album it belongs to are what one looks at), and it stays
  // open across photos on purpose - someone comparing a run of photos should
  // not reopen it on every arrow key. Closing it is remembered for the tab.
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
   * favorite toggled in here would not repaint until the grid refetches and
   * the viewer reopens. The overrides map is the viewer's own record of what
   * it changed, consulted before the snapshot — honest optimism, scoped to
   * this viewer session and discarded on close, by which time invalidation
   * has told the grid to catch up.
   */
  const [favoriteOverrides, setFavoriteOverrides] = useState<Record<string, boolean>>({});

  const favorite = useBulkFavorite();
  const trash = useBulkTrash();

  const isFavorite = asset ? (favoriteOverrides[asset.id] ?? asset.isFavorite) : false;

  const go = useCallback(
    (delta: number) => {
      const next = index + delta;
      if (next >= 0 && next < assets.length) onIndexChange(next);
    },
    [index, assets.length, onIndexChange],
  );

  const toggleFavorite = useCallback(() => {
    if (!asset) return;
    const next = !isFavorite;
    setFavoriteOverrides((current) => ({ ...current, [asset.id]: next }));
    favorite.mutate(
      { ids: [asset.id], isFavorite: next },
      {
        // Roll the optimistic flag back if the server said no, so the star
        // never lies about saved state.
        onError: () => setFavoriteOverrides((current) => ({ ...current, [asset.id]: !next })),
      },
    );
  }, [asset, isFavorite, favorite]);

  const confirmTrash = useCallback(() => {
    if (!asset) return;
    trash.mutate([asset.id], {
      onSuccess: () => {
        setConfirmingTrash(false);
        // The snapshot still contains the trashed asset, so "advance" means
        // moving the cursor rather than splicing the array — the grid drops
        // the asset on its own refetch. Last asset trashed: nothing sensible
        // left to show, so close.
        if (index < assets.length - 1) {
          onIndexChange(index + 1);
        } else if (index > 0) {
          onIndexChange(index - 1);
        } else {
          onClose();
        }
      },
    });
  }, [asset, trash, index, assets.length, onIndexChange, onClose]);

  useEffect(() => {
    setFullLoaded(false);
  }, [asset?.id]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      // While the trash confirm is up, IT owns the keyboard; paging photos
      // or closing the viewer underneath a modal question is chaos.
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
          toggleFavorite();
          break;
        case 'Delete':
          setConfirmingTrash(true);
          break;
        case 'i':
        case 'I':
          setShowInfo(!showInfo);
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
  }, [go, onClose, toggleFavorite, confirmingTrash, showInfo]);

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
      aria-label="Asset viewer"
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
          {index + 1} / {assets.length.toLocaleString()}
        </span>

        <span className="ml-auto text-sm text-white/60">
          {formatDate(asset.fileCreatedAt)}
          {asset.city ? ` · ${asset.city}` : ''}
          {asset.country ? `, ${asset.country}` : ''}
        </span>

        <button
          type="button"
          onClick={toggleFavorite}
          className={`rounded-md p-2 hover:bg-white/10 ${isFavorite ? 'text-accent' : 'hover:text-white'}`}
          aria-label={isFavorite ? 'Remove from favorites' : 'Add to favorites'}
          aria-pressed={isFavorite}
          title={`${isFavorite ? 'Unfavorite' : 'Favorite'} (F)`}
        >
          <svg width="20" height="20" viewBox="0 0 24 24" fill={isFavorite ? 'currentColor' : 'none'} stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round">
            <path d="m12 3 2.7 5.8 6.3.7-4.7 4.3 1.3 6.2L12 16.9 6.4 20l1.3-6.2L3 9.5l6.3-.7Z" />
          </svg>
        </button>

        <Link
          to="/faces"
          search={{ assetId: asset.id }}
          className="rounded-md p-2 hover:bg-white/10 hover:text-white"
          aria-label="Find this face across the library"
          title="Find face"
        >
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round">
            <circle cx="10" cy="8" r="4" />
            <path d="M3 20c0-3.5 3-6 7-6 1.2 0 2.3.2 3.2.6" />
            <circle cx="17.5" cy="16.5" r="3" />
            <path d="m19.8 18.8 2 2" />
          </svg>
        </Link>

        {asset.isImage && <CopyImageButton assetId={asset.id} />}

        <a
          href={assetOriginalUrl(asset.id)}
          download
          className="rounded-md p-2 hover:bg-white/10 hover:text-white"
          aria-label="Download original"
          title="Download original"
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
          aria-label={showInfo ? 'Hide photo information' : 'Show photo information'}
          title="Info (I)"
        >
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round">
            <circle cx="12" cy="12" r="9" />
            <path d="M12 11v5" />
            <circle cx="12" cy="7.75" r="0.9" fill="currentColor" stroke="none" />
          </svg>
        </button>

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
      </header>

      {/* A ROW, so the panel sits beside the photo rather than over it. The
          inner div keeps its own `relative`, which is what the nav buttons and
          the music panel position against — they must track the IMAGE, not the
          image-plus-sidebar. */}
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

            {/* Layer 3: the original. Large, and worth waiting for. If the
                browser cannot decode it, the thumbnail stays. */}
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
          <>
            <video
              key={asset.id}
              src={assetVideoUrl(asset.id)}
              poster={assetThumbnailUrl(asset.id)}
              controls
              autoPlay
              className="absolute inset-0 h-full w-full object-contain"
            />

            {/* Recognised music for this video. The panel renders null unless
                it has a match, so mounting it unconditionally is safe — and
                it floats above the video's native control strip. */}
            <div className="absolute bottom-20 left-4 z-10">
              <NowPlayingPanel assetId={asset.id} />
            </div>
          </>
        )}

        <NavButton side="left" onClick={() => go(-1)} disabled={index === 0} />
        <NavButton side="right" onClick={() => go(1)} disabled={index === assets.length - 1} />
        </div>

        {showInfo && (
          <InfoPanel
            // Keyed by asset so switching photos remounts the panel rather
            // than showing the previous photo's EXIF while the new request is
            // in flight — stale metadata beside a different image is worse
            // than a blank field.
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
          This item will move to the trash. Nothing is deleted permanently — you can restore
          it from the Trash page.
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
 * Copy the image to the clipboard, replacing the right-click → Copy image
 * dance (and the browser extension that used to paper over it).
 *
 * On an insecure origin the API does not exist at all, so rather than a button
 * that fails on click, this renders a disabled control whose tooltip says how
 * to get a working one. `canCopyImages()` is checked once per mount because
 * the answer cannot change without a navigation.
 *
 * The button reports its own outcome for a moment instead of raising a toast:
 * copying is a small, immediate act, and the feedback belongs where the click
 * happened. Videos never show it — `ClipboardItem` takes an image.
 */
function CopyImageButton({ assetId }: { assetId: string }) {
  const [state, setState] = useState<'idle' | 'copying' | 'copied' | 'failed'>('idle');
  const [message, setMessage] = useState<string | null>(null);
  const supported = canCopyImages();

  // Clear the transient result. Keyed on `state` so each result gets its own
  // full two seconds rather than inheriting a timer already part-elapsed.
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
        aria-label="Copy image (needs a secure connection)"
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
            // preview: this deployment serves the ORIGINAL for that size, which
            // is the quality anyone pasting into another app expects.
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
