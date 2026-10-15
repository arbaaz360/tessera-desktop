import { createPortal } from 'react-dom';
import { assetThumbnailUrl } from '@/shared/api/client';
import { useCoverCandidates, useSetCover } from './api';

/** How many candidates the flyout shows — the sidecar ranks far more, but the
 *  hover flow is for the obvious wins. The full modal has the rest. */
const STRIP_SIZE = 5;

const PANEL_GAP = 6;
const PANEL_PADDING = 8;
const VIEWPORT_MARGIN = 8;
/** Width of the ellipsis ("all candidates") column. */
const MORE_WIDTH = 36;

/**
 * Where the flyout goes: ADJACENT to the hovered card - directly above it when
 * there is room, directly below it otherwise.
 *
 * Three earlier rules, each fixing the last one's damage:
 *
 *   1. Anchored above the card. Near, but for a card low in the grid the panel
 *      landed mid-grid over the albums being browsed.
 *   2. Pinned to the top of the viewport. That stopped it covering mid-grid
 *      albums, but it covered the TOP ROW - including each card's top-left
 *      merge checkbox, making those albums impossible to select.
 *   3. Pinned to whichever viewport edge was furthest from the card. Nothing
 *      was covered any more, but on a tall window the panel appeared a long
 *      way from the thing it described, which is how it looked broken.
 *
 * The mistake in (2) and (3) was treating "must not cover the card" as "must
 * be far from the card". It only has to not OVERLAP it. Sitting immediately
 * above the card satisfies that completely - the card, its checkbox and its
 * cover all stay clear - while keeping the panel next to what it describes.
 *
 * It will overlap whatever sits above the card, which is fine: that is a
 * neighbouring album you are not pointing at, and the panel is gone the moment
 * the pointer leaves.
 *
 * Horizontal alignment follows the card, clamped on-screen, so there is a
 * visual thread back to what the panel belongs to.
 */
function panelPosition(anchor: DOMRect, panelWidth: number, panelHeight: number) {
  const viewportWidth = window.innerWidth;
  const viewportHeight = window.innerHeight;

  const left = Math.min(
    Math.max(anchor.left, VIEWPORT_MARGIN),
    Math.max(viewportWidth - panelWidth - VIEWPORT_MARGIN, VIEWPORT_MARGIN),
  );

  // A small gap so the panel never abuts the cover it describes.
  const GAP = 8;
  const above = anchor.top - GAP - panelHeight;
  const below = anchor.bottom + GAP;

  // Above when it fits on screen, otherwise below. Both keep the hovered card
  // itself uncovered, which is the only constraint that ever mattered.
  let top: number;
  if (above >= VIEWPORT_MARGIN) {
    top = above;
  } else if (below + panelHeight <= viewportHeight - VIEWPORT_MARGIN) {
    top = below;
  } else {
    // Neither side fits - a very short window. Take the roomier side and clamp,
    // accepting a partial overlap rather than pushing the panel off-screen.
    top =
      anchor.top > viewportHeight - anchor.bottom
        ? VIEWPORT_MARGIN
        : Math.max(viewportHeight - panelHeight - VIEWPORT_MARGIN, VIEWPORT_MARGIN);
  }

  return { top, left };
}

/**
 * The thumbnail edge actually used: the preference, shrunk if five thumbnails
 * plus chrome would not fit the viewport width. The slider sets desire; the
 * screen sets the ceiling.
 */
function fitThumb(preferred: number, tileCount: number): number {
  const available =
    window.innerWidth - VIEWPORT_MARGIN * 2 - PANEL_PADDING * 2 - MORE_WIDTH - (tileCount + 1) * PANEL_GAP;
  return Math.max(48, Math.min(preferred, Math.floor(available / Math.max(1, tileCount))));
}

/**
 * The top-candidates flyout that appears beside an album card on hover.
 *
 * This restores the old injected UI's signature move: rest on an album, see
 * its five best cover candidates AT A USEFUL SIZE, click one and the cover is
 * set — no modal, no page change. The full `CoverPickerModal` stays one click
 * away behind the ellipsis for when none of the five is right.
 *
 * Rendered `position: fixed` IN A PORTAL to document.body — both halves are
 * load-bearing. Content-sized fixed positioning is what gives the thumbnails
 * a judgeable size (the first version squeezed them into the card's ~55px
 * columns). And the portal is what makes `fixed` actually mean "the screen":
 * the virtualised grid positions its rows with `transform: translateY(...)`,
 * and a transformed ancestor becomes the containing block for fixed
 * descendants — rendered inside the row, the panel's viewport coordinates
 * were interpreted in the row's coordinate space and it landed off-screen.
 *
 * The portal moves the panel outside the card's DOM subtree, so the card's
 * mouseleave DOES fire when the pointer travels onto the panel. The
 * `onHoverChange` callbacks close that loop: the panel reports its own
 * enter/leave, and the card's grace timer treats card and panel as one hover
 * region.
 *
 * Clicking a candidate sets AND permanently locks the cover (the automatic
 * worker skips locked albums forever) — same semantics as the modal and the
 * old hover UI.
 */
export function HoverCandidates({
  albumId,
  currentCoverId,
  anchor,
  thumbSize,
  onMore,
  onHoverChange,
}: {
  albumId: string;
  currentCoverId: string | null;
  /** The spawning card's rect, measured when hover intent armed. */
  anchor: DOMRect;
  /** Preferred thumbnail edge in px — the albums page's persisted slider. */
  thumbSize: number;
  onMore: () => void;
  /** The card's grace-timer hooks: true = pointer entered, false = left. */
  onHoverChange: (inside: boolean) => void;
}) {
  const candidates = useCoverCandidates(albumId, true);
  const setCover = useSetCover(albumId);

  // A dead sidecar or an album the ranker has nothing for: show nothing.
  // The flyout is an enhancement — its failure mode is absence, not error UI.
  if (candidates.isError || (candidates.isSuccess && candidates.data.candidates.length === 0)) {
    return null;
  }

  const shown = candidates.data?.candidates.slice(0, STRIP_SIZE) ?? [];
  const tileCount = candidates.isPending ? STRIP_SIZE : shown.length;
  const thumb = fitThumb(thumbSize, tileCount);
  // Width from content: N thumbnails, the ellipsis column, gaps and padding.
  const panelWidth =
    tileCount * thumb + MORE_WIDTH + (tileCount + 1) * PANEL_GAP + PANEL_PADDING * 2;
  const panelHeight = thumb + PANEL_PADDING * 2;
  const { top, left } = panelPosition(anchor, panelWidth, panelHeight);

  return createPortal(
    <div
      role="group"
      aria-label="Top cover candidates"
      className="fixed z-50 rounded-lg border border-line bg-overlay shadow-2xl"
      style={{ top, left, padding: PANEL_PADDING }}
      onMouseEnter={() => onHoverChange(true)}
      onMouseLeave={() => onHoverChange(false)}
    >
      <div className="flex items-stretch" style={{ gap: PANEL_GAP }}>
        {candidates.isPending
          ? Array.from({ length: STRIP_SIZE }, (_, index) => (
              <div
                key={index}
                className="animate-pulse rounded-md bg-raised"
                style={{ width: thumb, height: thumb }}
              />
            ))
          : shown.map((candidate) => {
              const isCurrent = candidate.assetId === currentCoverId;
              return (
                <button
                  key={candidate.assetId}
                  type="button"
                  disabled={setCover.isPending}
                  onClick={(event) => {
                    // The card is wrapped in a Link; without this the click
                    // also navigates into the album mid-mutation.
                    event.preventDefault();
                    event.stopPropagation();
                    setCover.mutate(candidate.assetId);
                  }}
                  title={`Set as cover — rank ${candidate.rank}, score ${candidate.score.toFixed(2)}`}
                  aria-label={`Set ${candidate.fileName} as the album cover`}
                  className={`relative shrink-0 overflow-hidden rounded-md transition-all hover:ring-2 hover:ring-accent disabled:opacity-50 ${
                    isCurrent ? 'ring-2 ring-accent' : 'ring-1 ring-line'
                  }`}
                  style={{ width: thumb, height: thumb }}
                >
                  <img
                    src={assetThumbnailUrl(candidate.assetId)}
                    alt=""
                    loading="lazy"
                    decoding="async"
                    className="h-full w-full object-cover"
                  />
                  {isCurrent && (
                    <span className="absolute bottom-1 right-1 rounded bg-accent px-1 text-[10px] font-semibold text-ink-inverse">
                      current
                    </span>
                  )}
                </button>
              );
            })}

        <button
          type="button"
          onClick={(event) => {
            event.preventDefault();
            event.stopPropagation();
            onMore();
          }}
          title="All candidates"
          aria-label="Open the full cover picker"
          className="grid w-9 shrink-0 place-items-center rounded-md bg-raised text-ink-muted transition-colors hover:bg-surface hover:text-ink"
          style={{ height: thumb }}
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" aria-hidden>
            <circle cx="5" cy="12" r="1.7" fill="currentColor" />
            <circle cx="12" cy="12" r="1.7" fill="currentColor" />
            <circle cx="19" cy="12" r="1.7" fill="currentColor" />
          </svg>
        </button>
      </div>
    </div>,
    document.body,
  );
}
