import { useEffect, useRef } from 'react';
import { useImports, type ImportEntry } from './ImportContext';
import { summarizeCounts } from './api';

/** How long a finished import with nothing skipped stays on screen. */
const LINGER_MS = 8_000;

/**
 * One card per import, bottom right: what is being added and where, a bar
 * that moves with the items done, and the counts as they come in. A card
 * whose import skipped something stays until dismissed and lists why; one
 * that went cleanly leaves by itself after a few seconds.
 */
export function ImportToasts() {
  const { entries, dismiss } = useImports();
  if (entries.length === 0) return null;

  return (
    <div className="pointer-events-none fixed bottom-4 right-4 z-[55] flex w-[22rem] max-w-[calc(100vw-2rem)] flex-col gap-2">
      {entries.slice(-4).map((entry) => (
        <ImportToast key={entry.key} entry={entry} onDismiss={() => dismiss(entry.key)} />
      ))}
    </div>
  );
}

export function ImportToast({ entry, onDismiss }: { entry: ImportEntry; onDismiss: () => void }) {
  const job = entry.job;
  const finished = !!entry.note || !!entry.error || (job !== null && job.state !== 'running');
  const skipped = job?.items.filter((item) => item.outcome === 'skipped') ?? [];
  const lingers = finished && skipped.length === 0 && !entry.error;

  // A ref, so a card re-rendered by another import's progress keeps its timer.
  const dismissLater = useRef(onDismiss);
  dismissLater.current = onDismiss;
  useEffect(() => {
    if (!lingers) return;
    const timer = setTimeout(() => dismissLater.current(), LINGER_MS);
    return () => clearTimeout(timer);
  }, [lingers]);

  const where = entry.target ? `“${entry.target.albumName}”` : 'your library';
  const title = entry.note
    ? 'Nothing added'
    : entry.error
      ? `Could not add to ${where}`
      : !job
        ? `Adding to ${where}…`
        : job.state === 'running'
          ? `Adding ${job.total.toLocaleString()} ${job.total === 1 ? 'item' : 'items'} to ${where}`
          : `Added to ${where}`;

  const percent = job && job.total > 0 ? Math.round((job.done / job.total) * 100) : 0;

  return (
    <div
      role="status"
      aria-label={title}
      className="pointer-events-auto rounded-lg border border-line bg-overlay p-3 text-sm text-ink shadow-2xl"
    >
      <div className="flex items-start gap-2">
        <p className="min-w-0 flex-1 font-medium">{title}</p>
        <button
          type="button"
          onClick={onDismiss}
          aria-label="Dismiss"
          className="-mr-1 -mt-1 rounded p-1 text-ink-faint hover:bg-raised hover:text-ink"
        >
          ×
        </button>
      </div>

      {entry.note && <p className="mt-1 text-ink-muted">{entry.note}</p>}
      {entry.error && <p className="mt-1 text-danger">{entry.error}</p>}

      {job && (
        <>
          <div className="mt-2 h-1.5 overflow-hidden rounded-full bg-raised" aria-hidden>
            <div
              className={`h-full rounded-full transition-[width] duration-300 ${job.state === 'running' ? 'bg-accent' : 'bg-success'}`}
              style={{ width: `${job.state === 'running' ? percent : 100}%` }}
            />
          </div>
          <p className="mt-1.5 text-xs tabular-nums text-ink-muted">
            {job.done.toLocaleString()} of {job.total.toLocaleString()} · {summarizeCounts(job.counts)}
          </p>
          {folderLine(job.items)}
          {skipped.length > 0 && finished && (
            <ul className="mt-2 max-h-28 space-y-0.5 overflow-y-auto text-xs text-ink-faint">
              {skipped.slice(0, 20).map((item, index) => (
                <li key={`${item.source}-${index}`} className="truncate" title={`${item.source}: ${item.reason ?? ''}`}>
                  <span className="text-ink-muted">{fileName(item.source)}</span>: {item.reason ?? 'skipped'}
                </li>
              ))}
              {skipped.length > 20 && <li>and {(skipped.length - 20).toLocaleString()} more</li>}
            </ul>
          )}
        </>
      )}
    </div>
  );
}

/** Folders are indexed where they are; say how many pictures that found. */
function folderLine(items: { kind: string; outcome: string; pictures?: number }[]) {
  const folders = items.filter((item) => item.kind === 'folder' && item.outcome !== 'skipped' && item.outcome !== 'pending');
  if (folders.length === 0) return null;
  const pictures = folders.reduce((sum, item) => sum + (item.pictures ?? 0), 0);
  return (
    <p className="mt-0.5 text-xs tabular-nums text-ink-faint">
      {folders.length === 1 ? 'The folder stays where it is' : `${folders.length} folders stay where they are`}:{' '}
      {pictures.toLocaleString()} {pictures === 1 ? 'picture' : 'pictures'} in the library.
    </p>
  );
}

function fileName(source: string): string {
  const parts = source.split(/[\\/]/);
  return parts[parts.length - 1] || source;
}
