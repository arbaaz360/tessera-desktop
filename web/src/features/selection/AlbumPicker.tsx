import { useEffect, useMemo, useRef, useState } from 'react';
import { useAlbums } from '@/shared/api/queries';

/**
 * Searchable album picker for "Add to album".
 *
 * This library has 4,878 albums, which forces two decisions a small library
 * would never surface:
 *
 *   1. Filtering happens in memory over the already-cached album list
 *      (`useAlbums` holds it for five minutes) — no per-keystroke request.
 *   2. Rendering is CAPPED. A "match all" query would otherwise mount ~5,000
 *      rows into a popover; we show the first 50 and say how many more exist,
 *      which keeps typing responsive and nudges toward a narrower query.
 */
const MAX_RESULTS = 50;

export function AlbumPicker({
  onPick,
  disabled,
}: {
  onPick: (albumId: string) => void;
  /** True while the add mutation runs, so a double-click cannot fire twice. */
  disabled?: boolean;
}) {
  const { data: albums, isPending, isError } = useAlbums();
  const [query, setQuery] = useState('');
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => inputRef.current?.focus(), []);

  const { visible, hiddenCount } = useMemo(() => {
    if (!albums) return { visible: [], hiddenCount: 0 };
    const needle = query.trim().toLowerCase();
    const matches = needle
      ? albums.filter((album) => album.albumName.toLowerCase().includes(needle))
      : albums;
    return {
      visible: matches.slice(0, MAX_RESULTS),
      hiddenCount: Math.max(0, matches.length - MAX_RESULTS),
    };
  }, [albums, query]);

  return (
    <div className="flex w-72 flex-col overflow-hidden rounded-lg border border-line bg-overlay shadow-xl">
      <input
        ref={inputRef}
        type="text"
        value={query}
        onChange={(event) => setQuery(event.target.value)}
        placeholder="Search albums…"
        aria-label="Search albums"
        className="border-b border-line-soft bg-transparent px-3 py-2.5 text-sm text-ink placeholder:text-ink-faint focus:outline-none"
      />

      <div className="scrollbar-subtle max-h-64 overflow-y-auto py-1">
        {isPending && <p className="px-3 py-2 text-sm text-ink-faint">Loading albums…</p>}
        {isError && <p className="px-3 py-2 text-sm text-danger">Could not load albums.</p>}
        {!isPending && !isError && visible.length === 0 && (
          <p className="px-3 py-2 text-sm text-ink-faint">No albums match.</p>
        )}

        {visible.map((album) => (
          <button
            key={album.id}
            type="button"
            disabled={disabled}
            onClick={() => onPick(album.id)}
            className="flex w-full items-baseline gap-2 px-3 py-1.5 text-left text-sm text-ink transition-colors hover:bg-raised disabled:opacity-50"
          >
            <span className="min-w-0 flex-1 truncate">{album.albumName}</span>
            <span className="shrink-0 text-xs tabular-nums text-ink-faint">
              {album.assetCount.toLocaleString()}
            </span>
          </button>
        ))}

        {hiddenCount > 0 && (
          <p className="px-3 py-1.5 text-xs text-ink-faint">
            +{hiddenCount.toLocaleString()} more — keep typing to narrow.
          </p>
        )}
      </div>
    </div>
  );
}
