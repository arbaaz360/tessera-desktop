import { useEffect, useRef } from 'react';
import { Link } from '@tanstack/react-router';
import { formatBytes } from '@/shared/format';
import type { SearchStatus } from '@/shared/api/types';
import { apiErrorMessage } from '@/shared/api/tessera';
import { useSearchStatus, useStartModelDownload } from './api';

/**
 * Where the search model is, in numbers.
 *
 * Four states, each with the one action that fits:
 *
 *   turned off     → a link to Settings
 *   not downloaded → a button that starts the download (the one network use
 *                    besides the licence check and updates; said out loud)
 *   downloading    → bytes received of bytes total
 *   ready          → how many pictures have vectors, of how many there are
 *
 * Shared by onboarding, the Library page and the search page's 503 state, so
 * the same numbers appear wherever someone wonders "is search working yet".
 * Polls through `useSearchStatus` while anything is moving.
 */
export function SearchModelProgress({
  compact = false,
  onReady,
}: {
  /** Fewer words, for a card inside a bigger page. */
  compact?: boolean;
  /** Called once when the model reports ready. */
  onReady?: (() => void) | undefined;
}) {
  const status = useSearchStatus();
  const download = useStartModelDownload();

  // Fire `onReady` once per mount, from an effect: a refetch or a navigation
  // started during render would be a React warning at best and a loop at worst.
  const ready = status.data?.ready ?? false;
  const fired = useRef(false);
  useEffect(() => {
    if (ready && onReady && !fired.current) {
      fired.current = true;
      onReady();
    }
  }, [ready, onReady]);

  if (status.isPending) {
    return <p className="text-sm text-ink-faint">Checking the search model…</p>;
  }
  if (status.isError) {
    return (
      <p className="text-sm text-danger">Could not read the search status: {apiErrorMessage(status.error)}</p>
    );
  }

  const s = status.data;

  return (
    <div className="space-y-2">
      <StatusLine status={s} compact={compact} />

      {!s.enabled && (
        <p className="text-sm text-ink-muted">
          Search is turned off.{' '}
          <Link to="/settings" className="text-accent underline-offset-2 hover:underline">
            Turn it on in Settings
          </Link>
          .
        </p>
      )}

      {s.enabled && !s.ready && !s.downloading && (
        <div className="flex flex-wrap items-center gap-3">
          <button
            type="button"
            onClick={() => download.mutate()}
            disabled={download.isPending}
            className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-ink-inverse transition-colors hover:bg-accent-hover disabled:opacity-60"
          >
            {download.isPending ? 'Starting…' : 'Download the search model'}
          </button>
          {!compact && (
            <p className="text-xs text-ink-faint">
              About 190 MB, once, from the Tessera release server. It then runs on this machine, offline.
            </p>
          )}
          {download.isError && (
            <p className="text-xs text-danger">{apiErrorMessage(download.error)}</p>
          )}
        </div>
      )}

      {s.downloading && (
        <Bar
          value={s.downloading.receivedBytes}
          max={s.downloading.totalBytes}
          label={`${formatBytes(s.downloading.receivedBytes)} of ${formatBytes(s.downloading.totalBytes)} · ${s.downloading.file}`}
        />
      )}

      {s.ready && s.total > 0 && (
        <Bar
          value={s.embedded}
          max={s.total}
          label={`${s.embedded.toLocaleString()} of ${s.total.toLocaleString()} pictures indexed`}
        />
      )}
    </div>
  );
}

function StatusLine({ status, compact }: { status: SearchStatus; compact: boolean }) {
  const state = !status.enabled
    ? 'off'
    : status.downloading
      ? 'downloading'
      : status.ready
        ? status.embedded < status.total
          ? 'indexing'
          : 'ready'
        : 'not downloaded';

  return (
    <p className="text-sm text-ink">
      <span className="font-medium">{status.model}</span>
      <span className="text-ink-faint"> · {state}</span>
      {!compact && <span className="text-ink-faint"> · runs on {status.device}</span>}
    </p>
  );
}

/** A progress bar that also says the numbers: a bar alone is a feeling. */
export function Bar({ value, max, label }: { value: number; max: number; label: string }) {
  const fraction = max > 0 ? Math.min(1, value / max) : 0;
  return (
    <div>
      <div
        className="h-1.5 w-full overflow-hidden rounded-full bg-raised"
        role="progressbar"
        aria-valuenow={value}
        aria-valuemin={0}
        aria-valuemax={max}
        aria-label={label}
      >
        <div
          className="h-full rounded-full bg-accent transition-[width] duration-500"
          style={{ width: `${fraction * 100}%` }}
        />
      </div>
      <p className="mt-1 text-xs tabular-nums text-ink-faint">{label}</p>
    </div>
  );
}
