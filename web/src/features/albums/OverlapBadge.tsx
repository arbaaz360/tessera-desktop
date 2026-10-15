import { describeCounts, useAccountName, type OverlapEntry } from './overlap';

/**
 * The "this album exists in the other account too" marker.
 *
 * Deliberately ALWAYS VISIBLE rather than revealed on hover, unlike the
 * card's other affordances. The checkbox and pick-cover buttons are things you
 * go looking for; this is a fact about the album you need to know BEFORE you
 * decide to do anything to it — merging, moving or deleting an album whose
 * other half lives elsewhere is a different act from doing it to a whole one.
 *
 * It shows the OTHER side's item count, because the card already prints its
 * own count directly beneath. The tooltip carries the full picture: both
 * account names, and the image/video split on each side.
 *
 * NOT a link. The counterpart album belongs to another account, and Immich
 * scopes every read to the signed-in user — a link would 404 unless that album
 * happened to be shared. Naming the album and its size is the useful part; you
 * switch accounts to go there.
 */
export function OverlapBadge({
  entry,
  variant,
}: {
  entry: OverlapEntry;
  variant: 'card' | 'row';
}) {
  const accountName = useAccountName();
  const peerTotal = entry.peer.images + entry.peer.videos;
  const mineTotal = entry.mine.images + entry.mine.videos;

  const title =
    `“${entry.albumName}” also exists in ${accountName(entry.peer.ownerId)} ` +
    `with ${describeCounts(entry.peer)}. ` +
    `Here in ${accountName(entry.mine.ownerId)}: ${describeCounts(entry.mine)}.`;

  const label =
    `Also in ${accountName(entry.peer.ownerId)}: ${peerTotal.toLocaleString()} items. ` +
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
      // Sits on the thumbnail, bottom-left — the one corner no other control
      // claims (checkbox top-left, pick-cover top-right). The dark scrim keeps
      // it legible over a bright photo without a border fighting the artwork.
      className="pointer-events-auto absolute bottom-2 left-2 z-10 inline-flex items-center gap-1 rounded-md bg-black/65 px-1.5 py-0.5 text-[11px] font-semibold tabular-nums text-white backdrop-blur-[2px]"
    >
      {icon}
      {peerTotal.toLocaleString()}
    </span>
  );
}
