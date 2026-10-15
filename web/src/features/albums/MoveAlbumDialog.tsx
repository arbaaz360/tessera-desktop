import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import type { AlbumResponseDto } from '@immich/sdk';
import { qk, useMe } from '@/shared/api/queries';
import { overlapKeys, useAccounts, useAlbumOverlap, type OverlapEntry } from './overlap';
import { describeBatchMove, planMove, useAlbumMove, type BatchOutcome } from './move';

/**
 * Move the selected albums to another personal account.
 *
 * Works on any selection, one album or twenty. The only decision is WHERE:
 * with two personal accounts besides your own the dialog asks; with one it
 * confirms. What the move will do — which albums merge into existing ones —
 * comes from the overlap the grid already fetched, so nothing is looked up.
 *
 * A move is one row per album and finishes in the time it takes to read this,
 * so there are two states, not three: decide, then done.
 */
export function MoveAlbumDialog({
  albums,
  onClose,
}: {
  albums: AlbumResponseDto[];
  onClose: () => void;
}) {
  const { data: me } = useMe();
  const { data: accounts } = useAccounts();
  const overlap = useAlbumOverlap();
  const queryClient = useQueryClient();
  const { outcome, busy, begin } = useAlbumMove();

  // Destinations: every other personal account. A corpus is browsed, not
  // curated; nothing is moved into one.
  const targets = useMemo(
    () => (accounts ?? []).filter((a) => a.id !== me?.id && a.kind !== 'corpus'),
    [accounts, me?.id],
  );
  const [destOwnerId, setDestOwnerId] = useState<string | null>(null);
  const dest = targets.find((t) => t.id === destOwnerId) ?? (targets.length === 1 ? targets[0] : null);

  const plans = useMemo(
    () => (dest ? albums.map((album) => planMove(album, dest.id, overlap.get(album.id) ?? null)) : []),
    [albums, dest, overlap],
  );
  const totalAssets = plans.reduce((sum, plan) => sum + plan.assets, 0);
  const mergeCount = plans.filter((plan) => plan.willMerge).length;

  // Afterwards both accounts' album lists are wrong — invalidate rather than
  // patch; the counts come from the server.
  useEffect(() => {
    if (outcome?.state === 'done') {
      void queryClient.invalidateQueries({ queryKey: qk.albums.all });
      void queryClient.invalidateQueries({ queryKey: overlapKeys.all });
    }
  }, [outcome?.state, queryClient]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose, busy]);

  const title =
    albums.length === 1 ? `Move “${albums[0]?.albumName}”` : `Move ${albums.length} albums`;

  if (accounts && targets.length === 0) {
    return (
      <Shell onClose={onClose} title={title}>
        <p className="text-sm text-ink-muted">There is no other personal account to move to.</p>
      </Shell>
    );
  }

  return (
    <Shell onClose={onClose} title={title} dismissable={!busy}>
      {outcome ? (
        <OutcomeView outcome={outcome} destName={dest?.name ?? 'the other account'} onClose={onClose} />
      ) : (
        <>
          {targets.length > 1 && (
            <fieldset className="mb-3">
              <legend className="text-xs uppercase tracking-wide text-ink-faint">Move to</legend>
              <div className="mt-1.5 flex flex-wrap gap-2">
                {targets.map((t) => (
                  <label
                    key={t.id}
                    className={`cursor-pointer rounded-md border px-2.5 py-1 text-sm ${
                      dest?.id === t.id
                        ? 'border-accent bg-accent-muted text-ink'
                        : 'border-line text-ink-muted hover:bg-raised hover:text-ink'
                    }`}
                  >
                    <input
                      type="radio"
                      name="dest"
                      className="sr-only"
                      checked={dest?.id === t.id}
                      onChange={() => setDestOwnerId(t.id)}
                    />
                    {t.name}
                  </label>
                ))}
              </div>
            </fieldset>
          )}

          {!dest && <p className="text-sm text-ink-muted">Choose where these should go.</p>}

          {dest && (
            <>
              <p className="text-sm leading-relaxed text-ink-muted">
                {describeBatchMove(plans, dest.name)}
              </p>

              {plans.length > 1 && (
                <ul className="mt-3 max-h-48 space-y-1 overflow-y-auto rounded-md border border-line-soft p-2">
                  {plans.map((plan) => (
                    <li key={plan.albumId} className="flex items-baseline gap-2 text-xs">
                      <span className="min-w-0 flex-1 truncate text-ink" title={plan.albumName}>
                        {plan.albumName}
                      </span>
                      <span className="tabular-nums text-ink-faint">{plan.assets.toLocaleString()}</span>
                      {plan.willMerge ? (
                        <span
                          className="shrink-0 rounded border border-line px-1 text-[10px] font-medium uppercase tracking-wide text-ink-muted"
                          title={`${dest.name} already has “${plan.albumName}” holding ${plan.destAlbumAssets.toLocaleString()} items`}
                        >
                          merge → {plan.destAlbumAssets.toLocaleString()}
                        </span>
                      ) : (
                        <span className="shrink-0 text-[10px] uppercase tracking-wide text-ink-faint">
                          new
                        </span>
                      )}
                    </li>
                  ))}
                </ul>
              )}

              {mergeCount > 0 && (
                <p className="mt-3 rounded-md border border-line bg-raised px-3 py-2 text-xs leading-relaxed text-ink-muted">
                  <span className="font-semibold text-ink">
                    {mergeCount === 1 ? 'One of these merges.' : `${mergeCount} of these merge.`}
                  </span>{' '}
                  Both sides keep every picture — nothing is overwritten — but afterwards they
                  cannot be told apart without moving items back one by one.
                </p>
              )}

              <p className="mt-3 text-xs leading-relaxed text-ink-faint">
                The pictures stay where they are on disk, so they will still appear in this
                account&apos;s own timeline. Only the album changes hands.
              </p>

              {totalAssets === 0 && (
                <p className="mt-3 text-xs text-ink-faint">
                  Nothing in {albums.length === 1 ? 'this album' : 'these albums'} — the empty
                  {albums.length === 1 ? ' album' : ' albums'} will still move.
                </p>
              )}
            </>
          )}

          <div className="mt-4 flex justify-end gap-2">
            <button
              type="button"
              onClick={onClose}
              className="rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted hover:bg-raised hover:text-ink"
            >
              Cancel
            </button>
            <button
              type="button"
              disabled={!dest || busy}
              onClick={() =>
                dest &&
                void begin(
                  albums.map((album) => ({ id: album.id, albumName: album.albumName })),
                  dest.id,
                )
              }
              className="rounded-md bg-accent px-3 py-1.5 text-sm font-medium text-ink-inverse hover:bg-accent-hover disabled:opacity-40"
            >
              {busy ? 'Moving…' : dest ? `Move to ${dest.name}` : 'Move'}
            </button>
          </div>
        </>
      )}
    </Shell>
  );
}

function OutcomeView({
  outcome,
  destName,
  onClose,
}: {
  outcome: BatchOutcome;
  destName: string;
  onClose: () => void;
}) {
  return (
    <>
      {outcome.state === 'error' ? (
        <p className="text-sm text-danger">
          The move stopped after {outcome.albumsDone} of {outcome.albumsTotal}: {outcome.error}
        </p>
      ) : (
        <p className="text-sm text-ink">
          {outcome.state === 'done'
            ? `Moved ${outcome.albumsDone === 1 ? 'the album' : `${outcome.albumsDone} albums`} to ${destName}.`
            : `Moving to ${destName}… ${outcome.albumsDone} of ${outcome.albumsTotal}`}
        </p>
      )}

      <dl className="mt-3 flex gap-4 text-xs tabular-nums">
        <Stat label="items" value={outcome.moved} tone="text-success" />
        <Stat label="merged" value={outcome.merged} tone="text-ink-muted" />
      </dl>

      <div className="mt-4 flex justify-end">
        <button
          type="button"
          onClick={onClose}
          disabled={outcome.state === 'running'}
          className="rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted hover:bg-raised hover:text-ink disabled:opacity-40"
        >
          Done
        </button>
      </div>
    </>
  );
}

function Stat({ label, value, tone }: { label: string; value: number; tone: string }) {
  return (
    <div className="flex gap-1">
      <dt className="text-ink-faint">{label}</dt>
      <dd className={tone}>{value.toLocaleString()}</dd>
    </div>
  );
}

/**
 * `pointer-events-auto` is load-bearing: this renders inside the selection
 * bar's `pointer-events-none` positioning wrapper, and a dialog that inherits
 * that paints perfectly and ignores every click.
 */
function Shell({
  title,
  children,
  onClose,
  dismissable = true,
}: {
  title: string;
  children: ReactNode;
  onClose: () => void;
  dismissable?: boolean;
}) {
  return (
    <div className="pointer-events-auto fixed inset-0 z-[60] grid place-items-center bg-black/60 p-4 backdrop-blur-sm">
      <button
        type="button"
        aria-label="Close"
        disabled={!dismissable}
        onClick={onClose}
        className="absolute inset-0 cursor-default disabled:cursor-not-allowed"
      />
      <div
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="relative w-full max-w-lg rounded-lg border border-line bg-overlay p-5 shadow-2xl"
      >
        <h2 className="text-base font-semibold text-ink">{title}</h2>
        <div className="mt-3">{children}</div>
      </div>
    </div>
  );
}

export type { OverlapEntry };
