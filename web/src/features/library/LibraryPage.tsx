import { useState, type FormEvent } from 'react';
import { Link } from '@tanstack/react-router';
import { isTauri } from '@/shared/api/core';
import { pickFolder } from '@/shared/api/shell';
import { apiErrorMessage } from '@/shared/api/tessera';
import type { DuplicateSet, Library, LibraryRoot } from '@/shared/api/types';
import { formatBytes, formatDateTime } from '@/shared/format';
import { ConfirmDialog } from '@/features/selection/ConfirmDialog';
import { summarizeCounts } from '@/features/import/api';
import { Bar, SearchModelProgress } from './SearchModelProgress';
import { thumbnailsHeldByCap, useAddRoot, useDuplicates, useLibrary, useRemoveRoot, useScanRoot } from './api';

/**
 * The library: folders, progress, counts, drift.
 *
 * Everything on this page is a number. A scan reports what it saw, added,
 * moved and lost; the thumbnail and embedding queues report pending and done;
 * the watch reports its last tick. Status answers carry counts (the house
 * rule), so a drift of 3 and a drift of 300,000 never look the same.
 *
 * The page repolls while any of it is moving (see `useLibrary`) and settles
 * to a slow poll when it is not.
 */
export function LibraryPage() {
  const library = useLibrary();
  const [removing, setRemoving] = useState<LibraryRoot | null>(null);

  return (
    <div className="scrollbar-subtle h-full overflow-y-auto">
      <div className="mx-auto max-w-5xl px-4 py-6">
        <header className="mb-5 flex flex-wrap items-end justify-between gap-3">
          <div>
            <h1 className="text-xl font-semibold tracking-tight text-ink">Library</h1>
            <p className="mt-0.5 text-sm text-ink-faint">
              The folders Tessera watches, and what it has made of them.
            </p>
          </div>
          <AddFolder />
        </header>

        {library.isPending && <p className="text-sm text-ink-faint">Loading…</p>}
        {library.isError && (
          <p className="rounded-lg border border-danger/40 bg-danger/10 px-4 py-3 text-sm text-danger">
            Could not read the library: {apiErrorMessage(library.error)}
          </p>
        )}

        {library.data && (
          <div className="space-y-6">
            {library.data.licence.capReached && <CapBanner library={library.data} />}

            <section aria-label="Folders">
              <h2 className="mb-2 text-sm font-semibold text-ink">
                Folders <span className="font-normal text-ink-faint">· {library.data.roots.length}</span>
              </h2>
              <LastImport library={library.data} />
              {library.data.roots.length === 0 ? (
                <p className="rounded-lg border border-line bg-surface p-5 text-sm text-ink-muted">
                  No folders yet. Add one and Tessera starts reading it, or drop pictures onto the
                  window: those from outside your folders are copied into{' '}
                  <span className="break-all font-mono text-[12px]">{library.data.tesseraFolder?.path ?? 'Pictures/Tessera'}</span>.
                </p>
              ) : (
                <ul className="space-y-3">
                  {library.data.roots.map((root) => (
                    <RootCard key={root.id} root={root} onRemove={() => setRemoving(root)} />
                  ))}
                </ul>
              )}
            </section>

            <Progress library={library.data} />

            <Duplicates />
          </div>
        )}
      </div>

      {removing && <RemoveRootDialog root={removing} onClose={() => setRemoving(null)} />}
    </div>
  );
}

/**
 * The free tier stopped at the cap. Said with the numbers: how many are
 * indexed, how many the cap allows, and what is not happening to the rest.
 */
function CapBanner({ library }: { library: Library }) {
  const { licence } = library;
  return (
    <div
      role="status"
      className="flex flex-wrap items-center gap-4 rounded-lg border border-accent/50 bg-accent-muted px-4 py-3"
    >
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium text-ink">
          Free tier: {licence.indexed.toLocaleString()} pictures found, {licence.cap.toLocaleString()}{' '}
          indexed.
        </p>
        <p className="mt-0.5 text-xs text-ink-muted">
          Beyond the cap, pictures are recorded but get no thumbnail and no search until you unlock.
          Unlocking releases the queue; nothing is rescanned.
        </p>
      </div>
      <Link
        to="/settings"
        hash="licence"
        className="rounded-md bg-accent px-3 py-1.5 text-sm font-semibold text-ink-inverse transition-colors hover:bg-accent-hover"
      >
        Unlock
      </Link>
    </div>
  );
}

/** "Add folder": the system picker in Tauri, a path field in a browser. */
function AddFolder() {
  const addRoot = useAddRoot();
  const scan = useScanRoot();
  const [path, setPath] = useState('');
  const [open, setOpen] = useState(false);
  const tauri = isTauri();

  const add = async (folder: string) => {
    try {
      const root = await addRoot.mutateAsync(folder);
      scan.mutate(root.id, { onError: () => undefined });
      setPath('');
      setOpen(false);
    } catch {
      /* the mutation's error renders below */
    }
  };

  const choose = async () => {
    const folder = await pickFolder();
    if (folder) await add(folder);
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (path.trim()) void add(path.trim());
  };

  return (
    <div className="flex flex-col items-end gap-2">
      {tauri ? (
        <button
          type="button"
          onClick={() => void choose()}
          disabled={addRoot.isPending}
          className="rounded-md bg-accent px-3 py-1.5 text-sm font-semibold text-ink-inverse transition-colors hover:bg-accent-hover disabled:opacity-60"
        >
          {addRoot.isPending ? 'Adding…' : 'Add folder…'}
        </button>
      ) : open ? (
        <form onSubmit={submit} className="flex gap-2">
          <input
            autoFocus
            type="text"
            value={path}
            onChange={(event) => setPath(event.target.value)}
            placeholder="C:\Users\me\References"
            aria-label="Folder path"
            className="w-72 rounded-md border border-line bg-surface px-3 py-1.5 text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none"
          />
          <button
            type="submit"
            disabled={!path.trim() || addRoot.isPending}
            className="rounded-md bg-accent px-3 py-1.5 text-sm font-semibold text-ink-inverse transition-colors hover:bg-accent-hover disabled:opacity-60"
          >
            {addRoot.isPending ? 'Adding…' : 'Add'}
          </button>
          <button
            type="button"
            onClick={() => setOpen(false)}
            className="rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted hover:text-ink"
          >
            Cancel
          </button>
        </form>
      ) : (
        <button
          type="button"
          onClick={() => setOpen(true)}
          className="rounded-md bg-accent px-3 py-1.5 text-sm font-semibold text-ink-inverse transition-colors hover:bg-accent-hover"
        >
          Add folder…
        </button>
      )}
      {addRoot.isError && <p className="text-xs text-danger">{apiErrorMessage(addRoot.error)}</p>}
    </div>
  );
}

/** One folder: where it is, what is in it, what the last scan found. */
function RootCard({ root, onRemove }: { root: LibraryRoot; onRemove: () => void }) {
  const scan = useScanRoot();
  const last = root.lastScan;

  return (
    <li className="rounded-lg border border-line bg-surface p-4">
      <div className="flex flex-wrap items-start gap-3">
        <div className="min-w-0 flex-1">
          <p className="text-sm font-medium text-ink">
            {root.label}
            {root.appOwned && (
              <span className="ml-2 rounded border border-accent/50 px-1.5 py-0.5 align-middle text-[11px] font-normal text-accent">
                Tessera folder
              </span>
            )}
          </p>
          <p className="break-all font-mono text-[12px] text-ink-faint" title={root.path}>
            {root.path}
          </p>
          {root.appOwned && (
            <p className="mt-0.5 text-xs text-ink-faint">
              Pictures dropped or picked from outside your folders are copied here, one folder per album.
              The only folder Tessera writes to.
            </p>
          )}
        </div>
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => scan.mutate(root.id)}
            disabled={root.scanning || scan.isPending}
            className="rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted transition-colors hover:bg-raised hover:text-ink disabled:cursor-wait disabled:opacity-50"
          >
            {root.scanning ? 'Scanning…' : 'Scan now'}
          </button>
          <button
            type="button"
            onClick={onRemove}
            className="rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted transition-colors hover:border-danger hover:text-danger"
          >
            Remove
          </button>
        </div>
      </div>

      <dl className="mt-3 grid grid-cols-3 gap-3 text-sm sm:grid-cols-6">
        <Stat label="Files" value={root.counts.files} />
        <Stat label="Pictures" value={root.counts.contents} />
        <Stat label="In trash" value={root.counts.trashed} />
        {last && (
          <>
            <Stat label="Added" value={last.added} />
            <Stat label="Moved" value={last.moved} />
            <Stat label="Gone" value={last.gone} danger={last.gone > 0} />
          </>
        )}
      </dl>

      <p className="mt-3 text-xs tabular-nums text-ink-faint">
        {root.scanning
          ? 'A scan is running.'
          : last
            ? `Last scan ${formatDateTime(last.finishedAt ?? last.startedAt)}: ${last.seen.toLocaleString()} files seen` +
              (last.errors > 0 ? `, ${last.errors.toLocaleString()} errors` : '')
            : 'Not scanned yet.'}
        {!root.enabled && ' · Disabled'}
      </p>

      {scan.isError && <p className="mt-2 text-xs text-danger">{apiErrorMessage(scan.error)}</p>}
    </li>
  );
}

function Stat({ label, value, danger }: { label: string; value: number; danger?: boolean }) {
  return (
    <div>
      <dt className="text-[11px] uppercase tracking-wide text-ink-faint">{label}</dt>
      <dd className={`text-base font-semibold tabular-nums ${danger ? 'text-danger' : 'text-ink'}`}>
        {value.toLocaleString()}
      </dd>
    </div>
  );
}

/** Thumbnails, embeddings, the watch - as counts. */
function Progress({ library }: { library: Library }) {
  const thumbs = library.thumbnails;
  const embeds = library.embeddings;
  const held = thumbnailsHeldByCap(library);
  const thumbPending = thumbs.pending - held;
  const embedTotal = embeds.pending + embeds.done;

  return (
    <section aria-label="Progress" className="grid gap-3 md:grid-cols-3">
      <div className="rounded-lg border border-line bg-surface p-4">
        <h2 className="text-sm font-semibold text-ink">Thumbnails</h2>
        <div className="mt-3">
          <Bar
            value={thumbs.done}
            max={thumbs.done + thumbPending}
            label={
              `${thumbs.done.toLocaleString()} done · ${thumbPending.toLocaleString()} pending` +
              (held > 0 ? ` · ${held.toLocaleString()} held by the free tier` : '')
            }
          />
        </div>
      </div>

      <div className="rounded-lg border border-line bg-surface p-4">
        <h2 className="text-sm font-semibold text-ink">Search index</h2>
        <div className="mt-3">
          {embeds.ready ? (
            <Bar
              value={embeds.done}
              max={embedTotal}
              label={`${embeds.done.toLocaleString()} done · ${embeds.pending.toLocaleString()} pending · ${embeds.model}`}
            />
          ) : (
            <SearchModelProgress compact />
          )}
        </div>
      </div>

      <div className="rounded-lg border border-line bg-surface p-4">
        <h2 className="text-sm font-semibold text-ink">Watch</h2>
        <p className="mt-3 text-sm text-ink">
          {library.watch.running ? 'Watching your folders for changes.' : 'Not running.'}
        </p>
        <p className="mt-1 text-xs tabular-nums text-ink-faint">
          Last tick {formatDateTime(library.watch.lastTick)}
        </p>
      </div>
    </section>
  );
}

/**
 * Identical files in two or more places. A file's identity is its hash, so
 * two copies ARE one picture here; this is where the second copy's path is.
 */
function Duplicates() {
  const duplicates = useDuplicates();

  return (
    <section aria-label="Duplicates" className="rounded-lg border border-line bg-surface p-4">
      <header className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 className="text-sm font-semibold text-ink">
          Duplicates{' '}
          {duplicates.data && (
            <span className="font-normal text-ink-faint">
              · {duplicates.data.length.toLocaleString()} {duplicates.data.length === 1 ? 'picture' : 'pictures'}{' '}
              in more than one place
            </span>
          )}
        </h2>
        <p className="text-xs text-ink-faint">
          Same bytes, several files. Nothing is deleted; this only shows where the copies are.
        </p>
      </header>

      {duplicates.isPending && <p className="mt-3 text-sm text-ink-faint">Looking…</p>}
      {duplicates.isError && (
        <p className="mt-3 text-sm text-danger">{apiErrorMessage(duplicates.error)}</p>
      )}
      {duplicates.data && duplicates.data.length === 0 && (
        <p className="mt-3 text-sm text-ink-muted">No duplicates found.</p>
      )}
      {duplicates.data && duplicates.data.length > 0 && (
        <ul className="mt-3 divide-y divide-line-soft">
          {duplicates.data.map((set) => (
            <DuplicateRow key={set.contentId} set={set} />
          ))}
        </ul>
      )}
    </section>
  );
}

function DuplicateRow({ set }: { set: DuplicateSet }) {
  return (
    <li className="py-2.5">
      <p className="text-xs tabular-nums text-ink-faint">
        {set.locations.length} copies · {formatBytes(set.byteSize)} ·{' '}
        <span className="font-mono">{set.sha256.slice(0, 12)}</span>
      </p>
      <ul className="mt-1 space-y-0.5">
        {set.locations.map((location) => (
          <li key={location.id} className="break-all font-mono text-[12px] text-ink">
            {location.relPath}
          </li>
        ))}
      </ul>
    </li>
  );
}

/**
 * Removing a folder. The dialog says the one thing that matters: files are
 * untouched. The core forgets where the pictures are; the pictures stay.
 */
/** The newest drop, pick or folder add, in numbers. */
function LastImport({ library }: { library: Library }) {
  const last = library.imports?.last;
  if (!last) return null;
  const running = (library.imports?.running ?? 0) > 0;
  return (
    <p className="mb-2 text-xs tabular-nums text-ink-faint">
      {running ? 'Adding now. ' : ''}Last added {formatDateTime(last.finishedAt ?? last.startedAt)}:{' '}
      {last.total.toLocaleString()} {last.total === 1 ? 'item' : 'items'}, {summarizeCounts(last.counts)}.
    </p>
  );
}

function RemoveRootDialog({ root, onClose }: { root: LibraryRoot; onClose: () => void }) {
  const remove = useRemoveRoot();

  return (
    <ConfirmDialog
      title={`Remove “${root.label}” from the library?`}
      confirmLabel="Remove folder"
      danger
      busy={remove.isPending}
      onConfirm={() => remove.mutate(root.id, { onSuccess: onClose })}
      onCancel={onClose}
    >
      Tessera stops watching <span className="break-all font-mono text-[12px]">{root.path}</span> and
      forgets the {root.counts.contents.toLocaleString()} pictures it found there. The files on disk
      are not touched. Albums made from its folders go too, unless they still hold a picture from
      elsewhere; your own albums stay, and get their pictures back if the folder is added again.
      {remove.isError && <p className="mt-2 text-danger">{apiErrorMessage(remove.error)}</p>}
    </ConfirmDialog>
  );
}
