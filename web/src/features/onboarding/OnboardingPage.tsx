import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { Link, useNavigate } from '@tanstack/react-router';
import { useQueryClient } from '@tanstack/react-query';
import { isTauri } from '@/shared/api/core';
import { pickFolder } from '@/shared/api/shell';
import { apiErrorMessage } from '@/shared/api/tessera';
import { qk } from '@/shared/api/queries';
import { useAddRoot, useLibrary, useScanRoot } from '@/features/library/api';
import { SearchModelProgress } from '@/features/library/SearchModelProgress';
import { AddButtons } from '@/features/import/AddButtons';
import { useImportsIfMounted } from '@/features/import/ImportContext';
import { skipWelcome } from './welcome';

/**
 * First run.
 *
 * Three lines on what the app does, one folder, then the two things that
 * have to happen before the grid is worth looking at: the first scan and
 * the search model arriving. Both are shown as numbers moving, not as a
 * spinner. The moment the model reports ready the app opens the grid; a
 * "Continue" button is there for anyone who does not want to wait.
 *
 * In Tauri the folder comes from the system picker (`pick_folder`). In a
 * browser there is no picker, so a text field takes a path - the dialog-free
 * fallback the contract asks for.
 */
type Step = 'pick' | 'indexing';

export function OnboardingPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const library = useLibrary();
  const addRoot = useAddRoot();
  const scan = useScanRoot();

  const [step, setStep] = useState<Step>('pick');
  const [path, setPath] = useState('');
  const [error, setError] = useState<string | null>(null);
  const tauri = isTauri();

  const hasRoots = (library.data?.roots.length ?? 0) > 0;

  const addFolder = useCallback(
    async (folder: string) => {
      setError(null);
      try {
        const root = await addRoot.mutateAsync(folder);
        // The person chose the folder: that is the human start the scan
        // waits for. A 409 (already scanning) is not worth a message.
        scan.mutate(root.id, { onError: () => undefined });
        // Refetch before moving on, so the route gate sees the root.
        await queryClient.invalidateQueries({ queryKey: qk.library.all });
        setStep('indexing');
      } catch (cause) {
        setError(apiErrorMessage(cause));
      }
    },
    [addRoot, scan, queryClient],
  );

  const choose = async () => {
    const folder = await pickFolder();
    if (folder) await addFolder(folder);
  };

  const submitPath = (event: FormEvent) => {
    event.preventDefault();
    const folder = path.trim();
    if (folder) void addFolder(folder);
  };

  const openPictures = useCallback(() => void navigate({ to: '/photos' }), [navigate]);

  // Pictures dropped or picked here: once an import has added anything, the
  // grid is the place to see it.
  const imports = useImportsIfMounted();
  const addedSomething = (imports?.entries ?? []).some(
    (entry) =>
      entry.job &&
      entry.job.state !== 'running' &&
      entry.job.counts.added + entry.job.counts.copied + entry.job.counts.already + entry.job.counts.folders > 0,
  );
  useEffect(() => {
    if (step === 'pick' && addedSomething) {
      skipWelcome();
      void navigate({ to: '/photos' });
    }
  }, [step, addedSomething, navigate]);

  return (
    <div className="grid h-full place-items-center overflow-y-auto bg-ground px-6 py-10 text-ink">
      <div className="w-full max-w-lg">
        <div className="flex items-center gap-3">
          <span className="grid h-10 w-10 place-items-center rounded-lg bg-accent text-lg font-bold text-ink-inverse">
            T
          </span>
          <div>
            <h1 className="text-lg font-semibold tracking-tight">Tessera</h1>
            <p className="text-sm text-ink-faint">Your reference library, searchable.</p>
          </div>
        </div>

        <ul className="mt-8 space-y-3 text-sm leading-relaxed text-ink-muted">
          <li>
            <span className="font-medium text-ink">Your folders stay yours.</span> Tessera indexes
            your pictures where they are. Nothing is copied, renamed or deleted.
          </li>
          <li>
            <span className="font-medium text-ink">Find it by describing it.</span> “ink sketch of a
            horse”, “red dress on a beach”. The search runs on this machine, offline.
          </li>
          <li>
            <span className="font-medium text-ink">Move things freely.</span> A picture is its bytes:
            rename or move a file and its albums and search results follow it.
          </li>
        </ul>

        {step === 'pick' ? (
          <section className="mt-8 rounded-lg border border-line bg-surface p-5" aria-label="Choose a folder">
            <h2 className="text-base font-semibold">Choose a folder to index</h2>
            <p className="mt-1 text-sm text-ink-faint">
              A folder of your own pictures and references, for personal study. Every folder inside it
              becomes an album. You can add more later.
            </p>

            {tauri ? (
              <button
                type="button"
                onClick={() => void choose()}
                disabled={addRoot.isPending}
                className="mt-4 rounded-md bg-accent px-4 py-2 text-sm font-semibold text-ink-inverse transition-colors hover:bg-accent-hover disabled:opacity-60"
              >
                {addRoot.isPending ? 'Adding…' : 'Choose a folder…'}
              </button>
            ) : (
              <form onSubmit={submitPath} className="mt-4 flex gap-2">
                <input
                  type="text"
                  value={path}
                  onChange={(event) => setPath(event.target.value)}
                  placeholder="C:\Users\me\References"
                  aria-label="Folder path"
                  className="min-w-0 flex-1 rounded-md border border-line bg-ground px-3 py-2 text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none"
                />
                <button
                  type="submit"
                  disabled={!path.trim() || addRoot.isPending}
                  className="rounded-md bg-accent px-4 py-2 text-sm font-semibold text-ink-inverse transition-colors hover:bg-accent-hover disabled:opacity-60"
                >
                  {addRoot.isPending ? 'Adding…' : 'Add folder'}
                </button>
              </form>
            )}

            {error && (
              <p role="alert" className="mt-3 rounded-md bg-danger/10 px-3 py-2 text-sm text-danger">
                {error}
              </p>
            )}

            <div className="mt-5 border-t border-line-soft pt-4">
              <p className="text-sm text-ink-muted">
                Or start with a few pictures: drag them onto this window, or pick them. Those already
                in a folder you add later are not copied twice.
              </p>
              <div className="mt-3 flex flex-wrap items-center gap-2">
                {imports && <AddButtons target={null} variant="empty" />}
                <button
                  type="button"
                  onClick={() => {
                    skipWelcome();
                    openPictures();
                  }}
                  className="rounded-md px-3 py-2 text-sm text-ink-faint underline-offset-2 hover:text-ink hover:underline"
                >
                  Skip for now
                </button>
              </div>
            </div>

            {hasRoots && (
              <p className="mt-4 text-sm text-ink-faint">
                {library.data!.roots.length} {library.data!.roots.length === 1 ? 'folder is' : 'folders are'}{' '}
                already in your library.{' '}
                <Link to="/photos" className="text-accent underline-offset-2 hover:underline">
                  Open your pictures
                </Link>
              </p>
            )}
          </section>
        ) : (
          <section className="mt-8 space-y-4" aria-label="Getting ready">
            <div className="rounded-lg border border-line bg-surface p-5">
              <h2 className="text-base font-semibold">Reading your folder</h2>
              {library.data?.roots.map((root) => (
                <p key={root.id} className="mt-1 text-sm text-ink-muted">
                  <span className="break-all font-mono text-[12px] text-ink">{root.path}</span>
                  <br />
                  <span className="tabular-nums">
                    {root.counts.files.toLocaleString()} files seen
                    {root.scanning ? ', still looking…' : root.lastScan ? ', scan finished' : ''}
                  </span>
                </p>
              ))}
              {library.data && (
                <p className="mt-2 text-xs tabular-nums text-ink-faint">
                  Thumbnails: {library.data.thumbnails.done.toLocaleString()} done,{' '}
                  {library.data.thumbnails.pending.toLocaleString()} to go
                </p>
              )}
            </div>

            <div className="rounded-lg border border-line bg-surface p-5">
              <h2 className="text-base font-semibold">Search model</h2>
              <p className="mt-1 mb-3 text-sm text-ink-faint">
                The one download. Once it is here, searching never leaves this machine.
              </p>
              <SearchModelProgress onReady={openPictures} />
            </div>

            <div className="flex items-center gap-3">
              <button
                type="button"
                onClick={openPictures}
                className="rounded-md bg-accent px-4 py-2 text-sm font-semibold text-ink-inverse transition-colors hover:bg-accent-hover"
              >
                Open your pictures
              </button>
              <button
                type="button"
                onClick={() => {
                  setPath('');
                  setStep('pick');
                }}
                className="text-sm text-ink-muted underline-offset-2 hover:text-ink hover:underline"
              >
                Add another folder
              </button>
            </div>
          </section>
        )}
      </div>
    </div>
  );
}
