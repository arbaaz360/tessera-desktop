import { describeCounts, type OverlapEntry } from './overlap';

/**
 * The "another album has this name too" marker.
 *
 * Deliberately ALWAYS VISIBLE rather than revealed on hover, unlike the
 * card's other affordances: this is a fact about the album you need to know
 * BEFORE you decide to do anything to it. It shows the OTHER album's item
 * count, because the card already prints its own count directly beneath. The
 * tooltip carries the full picture: the image/video split on each side.
 */
export function OverlapBadge({
  entry,
  variant,
}: {
  entry: OverlapEntry;
  variant: 'card' | 'row';
}) {
  const peerTotal = entry.peer.images + entry.peer.videos;
  const mineTotal = entry.mine.images + entry.mine.videos;

  const title =
    `Another album is also called "${entry.albumName}", with ${describeCounts(entry.peer)}. ` +
    `This one holds ${describeCounts(entry.mine)}.`;

  const label =
    `Another album shares this name: ${peerTotal.toLocaleString()} items. ` +
    `This one holds ${mineTotal.toLocaleString()}.`;

  const icon = (
    <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" aria-hidden>
      <path d="M3 8h14M13 4l4 4-4 4M21 16H7M11 12l-4 4 4 4" />
    </svg>
  );

  if (variant === 'row') {
    return (
      <span
        title={title}
        aria-label={label}
        className="inline-flex shrink-0 items-center gap-1 rounded-md border border-accent/40 bg-accent-muted px-1.5 py-0.5 text-[11px] font-medium tabular-nums text-accent"
      >
        {icon}
        {peerTotal.toLocaleString()}
      </span>
    );
  }

  return (
    <span
      title={title}
      aria-label={label}
      // Sits on the thumbnail, bottom-left - the one corner no other control
      // claims (pick-cover top-right). The dark scrim keeps it legible over a
      // bright photo without a border fighting the artwork.
      className="pointer-events-auto absolute bottom-2 left-2 z-10 inline-flex items-center gap-1 rounded-md bg-black/65 px-1.5 py-0.5 text-[11px] font-semibold tabular-nums text-white backdrop-blur-[2px]"
    >
      {icon}
      {peerTotal.toLocaleString()}
    </span>
  );
}
