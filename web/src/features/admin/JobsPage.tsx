import { useEffect, useRef, useState } from 'react';
import type { QueueResponseDto } from '@immich/sdk';
import { apiErrorMessage, useQueueActions, useQueues, useScanFolder, useSystemConfig, useSetQueueConcurrency, useWaitingFolders, type WaitingFolder } from './api';
import {
  CONCURRENCY_QUEUES,
  FORCELESS_QUEUES,
  STARTABLE_QUEUES,
  SWITCH_QUEUES,
  UNPAUSABLE_QUEUES,
  pendingCount,
  queueHint,
  queueLabel,
} from './queues';

/**
 * Jobs: live queue depths and manual runs.
 *
 * This page exists for one moment in particular: the days after cutover, when
 * the smart-search queue holds a 292k-asset CLIP backfill and the only
 * question that matters is "is the number going down?". So the layout is built
 * around that reading posture — the pending count is the biggest thing on
 * every card, everything numeric is `tabular-nums` so digits do not jitter as
 * they change, and the whole page repolls every five seconds while visible.
 *
 * Queues render in server order (the fork's own enum order) rather than
 * sorted by depth: rows that swap positions between polls are impossible to
 * watch.
 */
export function JobsPage() {
  const { data: queues, isPending, isError, error } = useQueues();

  return (
    <div className="scrollbar-subtle h-full overflow-y-auto">
      <div className="mx-auto max-w-6xl px-4 py-6">
        <header className="mb-5 flex flex-wrap items-end justify-between gap-3">
          <div>
            <h1 className="text-xl font-semibold tracking-tight text-ink">Jobs</h1>
            <p className="mt-0.5 text-sm text-ink-faint">
              Refreshes every 5 seconds while this page is open.
            </p>
          </div>

          {queues && <TotalPending queues={queues} />}
        </header>

        {isPending && <QueuesSkeleton />}

        {isError && (
          <div className="rounded-lg border border-line bg-surface p-6 text-center">
            <p className="text-ink">Could not load the queues.</p>
            <p className="mt-1 text-sm text-ink-faint">{apiErrorMessage(error)}</p>
          </div>
        )}

        {queues && (
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
            {queues.map((queue) => (
              <QueueCard key={queue.name} queue={queue} />
            ))}
          </div>
        )}

        <WaitingFolders />
      </div>
    </div>
  );
}

/**
 * What the library watch has noticed. A folder copied into a library is
 * scanned on its own once it settles and it is small; a big one is heavy
 * work, and heavy work is started by a person - here.
 */
function WaitingFolders() {
  const { data } = useWaitingFolders();
  const scan = useScanFolder();
  if (!data || data.folders.length === 0) return null;

  const waiting = data.folders.filter((f) => f.waitingForYou);
  const settling = data.folders.length - waiting.length;

  return (
    <section aria-label="Folders waiting for a scan" className="mt-6 rounded-lg border border-line bg-surface p-4">
      <header className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 className="text-sm font-semibold text-ink">Folders the watch has noticed</h2>
        <p className="text-xs text-ink-faint">
          {settling > 0 && `${settling} settling or queued to scan on their own`}
          {settling > 0 && waiting.length > 0 && ' · '}
          {waiting.length > 0 &&
            `${waiting.length} over ${data.autoScanMaxEntries.toLocaleString()} entries - too big to start unasked`}
        </p>
      </header>

      {waiting.length > 0 && (
        <ul className="mt-3 divide-y divide-line">
          {waiting.map((f) => (
            <FolderRow key={`${f.rootId}:${f.folder}`} folder={f} onScan={() => scan.mutate({ rootId: f.rootId, folder: f.folder })} busy={scan.isPending} />
          ))}
        </ul>
      )}
      {scan.error && <p className="mt-2 text-xs text-danger">{apiErrorMessage(scan.error)}</p>}
    </section>
  );
}

function FolderRow({ folder, onScan, busy }: { folder: WaitingFolder; onScan: () => void; busy: boolean }) {
  return (
    <li className="flex items-center justify-between gap-3 py-2">
      <div className="min-w-0">
        <p className="truncate text-sm text-ink">{folder.folder}</p>
        <p className="text-xs text-ink-faint">
          {folder.rootLabel.split(':')[0]} · {(folder.entries ?? 0).toLocaleString()} entries · noticed{' '}
          {new Date(folder.changedAt).toLocaleString()}
        </p>
      </div>
      <button
        type="button"
        onClick={onScan}
        disabled={busy}
        className="shrink-0 rounded border border-line px-2.5 py-1 text-xs font-medium text-ink hover:bg-line disabled:opacity-50"
      >
        Scan now
      </button>
    </li>
  );
}

/**
 * The one number summarising the whole system. During the backfill this reads
 * "292,041 jobs pending" and counts down; when it says zero, the migration is
 * done.
 */
function TotalPending({ queues }: { queues: QueueResponseDto[] }) {
  const total = queues.reduce((sum, queue) => sum + pendingCount(queue.statistics), 0);

  return (
    <div className="text-right">
      <p className="text-2xl font-semibold tabular-nums tracking-tight text-ink">
        {total.toLocaleString()}
      </p>
      <p className="text-xs text-ink-faint">jobs pending across all queues</p>
    </div>
  );
}

function QueueCard({ queue }: { queue: QueueResponseDto }) {
  const { name, isPaused, statistics } = queue;
  const { start, setPaused, clearFailed } = useQueueActions(name);
  const [confirmingForce, setConfirmingForce] = useState(false);

  // The modern QueueResponseDto dropped the legacy `isActive` flag, but the
  // server itself derived it as `statistics.active > 0` (see the fork's
  // mapQueueLegacy), so deriving it the same way here loses nothing.
  const isActive = statistics.active > 0;
  const pending = pendingCount(statistics);
  const hint = queueHint(name);

  // Whichever control failed most recently owns the error line. Checking in
  // reverse click-likelihood order is crude but honest: only one message fits,
  // and the newest failure is the one being wondered about.
  const failure = start.error ?? setPaused.error ?? clearFailed.error;

  return (
    <section
      aria-label={queueLabel(name)}
      className="flex flex-col rounded-lg border border-line bg-surface p-4"
    >
      <header className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <h2 className="flex items-center gap-2 text-sm font-semibold text-ink">
            <StatusDot isActive={isActive} isPaused={isPaused} />
            <span className="truncate">{queueLabel(name)}</span>
          </h2>
          {hint && <p className="mt-0.5 text-xs text-ink-faint">{hint}</p>}
        </div>

        {isPaused && (
          <span className="shrink-0 rounded bg-accent-muted px-1.5 py-0.5 text-[11px] font-medium text-accent">
            Paused
          </span>
        )}
      </header>

      <div className="mt-3 flex items-end justify-between gap-4">
        <div>
          <p
            className={`text-3xl font-semibold tabular-nums tracking-tight ${
              pending > 0 ? 'text-ink' : 'text-ink-faint'
            }`}
          >
            {pending.toLocaleString()}
          </p>
          <p className="text-xs text-ink-faint">pending</p>
        </div>

        <dl className="grid grid-cols-3 gap-x-4 text-right">
          <Stat label="active" value={statistics.active} emphasis={isActive ? 'success' : 'none'} />
          <Stat label="waiting" value={statistics.waiting} emphasis="none" />
          <Stat
            label="failed"
            value={statistics.failed}
            emphasis={statistics.failed > 0 ? 'danger' : 'none'}
          />
        </dl>
      </div>

      <div className="mt-4 flex flex-wrap items-center gap-1.5 border-t border-line-soft pt-3">
        {STARTABLE_QUEUES.has(name) && (
          <>
            <CardButton
              onClick={() => start.mutate(false)}
              disabled={start.isPending || (SWITCH_QUEUES.has(name) && !isPaused)}
              title={
                SWITCH_QUEUES.has(name)
                  ? 'Switch on: work through everything queued, then switch off by itself'
                  : 'Queue only assets that have not been processed yet'
              }
            >
              {start.isPending ? 'Starting…' : SWITCH_QUEUES.has(name) ? (isPaused ? 'Run' : 'Running') : 'Run missing'}
            </CardButton>

            {!FORCELESS_QUEUES.has(name) && (
              <CardButton
                onClick={() => setConfirmingForce(true)}
                disabled={start.isPending}
                title="Re-process every asset, including ones already done"
              >
                Force all…
              </CardButton>
            )}
          </>
        )}

        {!UNPAUSABLE_QUEUES.has(name) && !(SWITCH_QUEUES.has(name) && isPaused) && (
          <CardButton onClick={() => setPaused.mutate(!isPaused)} disabled={setPaused.isPending}>
            {setPaused.isPending ? 'Working…' : SWITCH_QUEUES.has(name) ? 'Stop' : isPaused ? 'Resume' : 'Pause'}
          </CardButton>
        )}

        {statistics.failed > 0 && (
          <CardButton onClick={() => clearFailed.mutate()} disabled={clearFailed.isPending} tone="danger">
            {clearFailed.isPending
              ? 'Clearing…'
              : `Clear ${statistics.failed.toLocaleString()} failed`}
          </CardButton>
        )}
      </div>

      {CONCURRENCY_QUEUES.has(name) && <ConcurrencyControl queue={name} />}

      {failure != null && (
        <p role="alert" className="mt-2 rounded-md bg-danger/10 px-2.5 py-1.5 text-xs text-danger">
          {apiErrorMessage(failure)}
        </p>
      )}

      {confirmingForce && (
        <ConfirmDialog
          title={`Force-run ${queueLabel(name)}?`}
          body="This re-queues every asset in the library — all 1.6 million — not just the unprocessed ones. On this hardware a forced run takes DAYS to drain and cannot be un-queued without emptying the queue. Only do this if the existing results are actually wrong."
          confirmLabel="Re-process everything"
          onConfirm={() => {
            setConfirmingForce(false);
            start.mutate(true);
          }}
          onCancel={() => setConfirmingForce(false)}
        />
      )}
    </section>
  );
}

function Stat({
  label,
  value,
  emphasis,
}: {
  label: string;
  value: number;
  emphasis: 'none' | 'success' | 'danger';
}) {
  const tone =
    emphasis === 'danger' ? 'text-danger' : emphasis === 'success' ? 'text-success' : 'text-ink-muted';

  return (
    <div>
      <dd className={`text-sm font-medium tabular-nums ${tone}`}>{value.toLocaleString()}</dd>
      <dt className="text-[11px] text-ink-faint">{label}</dt>
    </div>
  );
}

/**
 * How many jobs of this kind run at once.
 *
 * This is the ONLY throttle Immich has for background work. There is no rate
 * limit and no "gentle mode": a queue at concurrency 1 still runs flat out, it
 * just runs one job at a time. So for something GPU-heavy like the smart-search
 * backfill, the real controls are this number and Pause — which the caption says
 * out loud, because otherwise people go hunting for a slider that does not exist.
 *
 * Rendered per queue rather than on a settings page so the number sits beside
 * the thing it governs, next to the pending count that shows its effect.
 */
function ConcurrencyControl({ queue }: { queue: string }) {
  const config = useSystemConfig();
  const setConcurrency = useSetQueueConcurrency();

  const current = (config.data?.job as Record<string, { concurrency?: number }> | undefined)?.[queue]
    ?.concurrency;

  if (current === undefined) return null;

  return (
    <div className="mt-3 flex flex-wrap items-center gap-2 border-t border-line-soft pt-3">
      <label className="flex items-center gap-2 text-xs text-ink-faint">
        Parallel workers
        <input
          type="number"
          min={1}
          max={16}
          defaultValue={current}
          disabled={setConcurrency.isPending}
          onBlur={(event) => {
            const next = Number(event.target.value);
            if (Number.isInteger(next) && next >= 1 && next <= 16 && next !== current) {
              setConcurrency.mutate({ queue, concurrency: next });
            }
          }}
          className="w-16 rounded-md border border-line bg-surface px-2 py-1 text-sm tabular-nums text-ink focus:border-accent focus:outline-none"
          aria-label={`Parallel workers for the ${queue} queue`}
        />
      </label>

      <span className="text-xs text-ink-faint">
        {setConcurrency.isPending
          ? 'Saving…'
          : current === 1
            ? 'lowest setting — use Pause to stop it entirely'
            : 'higher is faster and heavier on CPU/GPU'}
      </span>

      {setConcurrency.error && (
        <span className="text-xs text-danger">{apiErrorMessage(setConcurrency.error)}</span>
      )}
    </div>
  );
}

function StatusDot({ isActive, isPaused }: { isActive: boolean; isPaused: boolean }) {
  if (isActive) {
    return <span className="h-2 w-2 shrink-0 animate-pulse rounded-full bg-success" aria-hidden />;
  }
  if (isPaused) {
    return <span className="h-2 w-2 shrink-0 rounded-full bg-accent" aria-hidden />;
  }
  return <span className="h-2 w-2 shrink-0 rounded-full bg-line" aria-hidden />;
}

function CardButton({
  onClick,
  disabled,
  title,
  tone = 'neutral',
  children,
}: {
  onClick: () => void;
  disabled?: boolean;
  title?: string;
  tone?: 'neutral' | 'danger';
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      title={title}
      className={`rounded-md border px-2.5 py-1 text-xs font-medium transition-colors disabled:opacity-50 ${
        tone === 'danger'
          ? 'border-danger/40 text-danger hover:bg-danger/10'
          : 'border-line text-ink-muted hover:bg-raised hover:text-ink'
      }`}
    >
      {children}
    </button>
  );
}

/**
 * A modal confirmation, hand-rolled because the app has no dialog dependency
 * and one confirm does not justify adding one.
 *
 * It is an `alertdialog` (the response is consequential), Escape cancels, and
 * focus starts on Cancel so a reflexive Enter does the safe thing. Focus is
 * not fully trapped — a Tab past the two buttons can escape — which is an
 * accepted gap for a two-button dialog rather than a reason to hand-write a
 * focus trap badly.
 */
function ConfirmDialog({
  title,
  body,
  confirmLabel,
  onConfirm,
  onCancel,
}: {
  title: string;
  body: string;
  confirmLabel: string;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const cancelRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    cancelRef.current?.focus();

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onCancel();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [onCancel]);

  return (
    <div className="fixed inset-0 z-50 grid place-items-center p-4">
      <button
        type="button"
        aria-label="Cancel"
        className="absolute inset-0 bg-black/60"
        onClick={onCancel}
      />

      <div
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="confirm-title"
        className="relative w-full max-w-md rounded-lg border border-line bg-overlay p-5 shadow-xl"
      >
        <h3 id="confirm-title" className="text-base font-semibold text-ink">
          {title}
        </h3>
        <p className="mt-2 text-sm leading-relaxed text-ink-muted">{body}</p>

        <div className="mt-5 flex justify-end gap-2">
          <button
            ref={cancelRef}
            type="button"
            onClick={onCancel}
            className="rounded-md border border-line px-3 py-1.5 text-sm font-medium text-ink-muted transition-colors hover:bg-raised hover:text-ink"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={onConfirm}
            className="rounded-md bg-danger px-3 py-1.5 text-sm font-semibold text-white transition-colors hover:opacity-90"
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}

function QueuesSkeleton() {
  return (
    <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
      {Array.from({ length: 9 }, (_, index) => (
        <div
          key={index}
          className="h-40 animate-pulse rounded-lg bg-raised"
          style={{ animationDelay: `${index * 35}ms` }}
        />
      ))}
    </div>
  );
}
