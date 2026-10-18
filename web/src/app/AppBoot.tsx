import { useCallback, useEffect, useRef, useState } from 'react';
import { RouterProvider } from '@tanstack/react-router';
import { configureApi, type CoreInfo } from '@/shared/api/client';
import { CoreUnavailableError, isCoreReady, resolveCore } from '@/shared/api/core';
import { useFollowCoreRestarts } from '@/shared/api/coreRestarts';
import { ImportProvider } from '@/features/import/ImportContext';
import { DropZone } from '@/features/import/DropZone';
import { ImportToasts } from '@/features/import/ImportToasts';
import { queryClient } from './queryClient';
import { router } from './router';

/**
 * The boot sequence, as a component.
 *
 *   1. Find the core (shell in Tauri, `.env.local` in a browser).
 *   2. Tell the API layer where it is.
 *   3. Wait for `/health/ready` to answer 200.
 *   4. Mount the router.
 *
 * Until step 4 the person sees "Starting the engine", not a broken grid. The
 * core takes a moment on first launch (migrating the database, starting the
 * workers), and on a cold machine longer than that; polling once a second
 * with a retry button costs nothing and turns a mystery into a wait.
 *
 * Two distinct failures get two distinct screens: "no core configured" is a
 * developer's problem (the env file), "not ready yet" is a moment's patience.
 *
 * After boot the shell may restart the core on a new port with a new token
 * (`core-ready`); the app follows it and remounts under the new address
 * rather than talking to a dead port until someone restarts it.
 */

type Phase =
  | { kind: 'resolving' }
  | { kind: 'waiting'; core: CoreInfo; attempts: number }
  | { kind: 'ready' }
  | { kind: 'no-core'; message: string };

const POLL_MS = 1_000;
/** After this many quiet seconds the screen explains itself a little more. */
const SLOW_AFTER_ATTEMPTS = 8;

export function AppBoot() {
  const [phase, setPhase] = useState<Phase>({ kind: 'resolving' });
  const cancelled = useRef(false);

  const boot = useCallback(async () => {
    setPhase({ kind: 'resolving' });
    let core: CoreInfo;
    try {
      core = await resolveCore();
    } catch (error) {
      setPhase({
        kind: 'no-core',
        message:
          error instanceof CoreUnavailableError
            ? error.message
            : `Could not reach the shell: ${(error as Error).message}`,
      });
      return;
    }
    configureApi(core);

    let attempts = 0;
    while (!cancelled.current) {
      if (await isCoreReady(core)) {
        setPhase({ kind: 'ready' });
        return;
      }
      attempts += 1;
      setPhase({ kind: 'waiting', core, attempts });
      await new Promise((resolve) => setTimeout(resolve, POLL_MS));
    }
  }, []);

  useEffect(() => {
    cancelled.current = false;
    void boot();
    return () => {
      cancelled.current = true;
    };
  }, [boot]);

  const generation = useFollowCoreRestarts(phase.kind === 'ready', () => {
    // Whatever failed while the core was down is worth asking again.
    void queryClient.invalidateQueries();
  });

  // Imports live above the router: a drop works on every page, the welcome
  // page included, and an import keeps going while the person navigates.
  if (phase.kind === 'ready') {
    return (
      <ImportProvider key={generation}>
        <RouterProvider router={router} />
        <DropZone />
        <ImportToasts />
      </ImportProvider>
    );
  }

  return (
    <div className="grid h-full place-items-center bg-ground px-6 text-center text-ink">
      <div className="max-w-md">
        <Mark />

        {phase.kind === 'no-core' ? (
          <>
            <h1 className="mt-5 text-lg font-semibold tracking-tight">No engine to talk to</h1>
            <p className="mt-2 text-sm leading-relaxed text-ink-muted">{phase.message}</p>
          </>
        ) : (
          <>
            <h1 className="mt-5 text-lg font-semibold tracking-tight">Starting the engine</h1>
            <p className="mt-2 text-sm leading-relaxed text-ink-muted" role="status">
              {phase.kind === 'resolving'
                ? 'Finding the library engine…'
                : phase.attempts < SLOW_AFTER_ATTEMPTS
                  ? 'Opening the library database and starting the workers.'
                  : `Still waiting after ${phase.attempts} seconds. The engine at ${phase.core.baseUrl} has not answered /health/ready yet. If this is the first launch on a large library it can take a while; otherwise try again.`}
            </p>
          </>
        )}

        <button
          type="button"
          onClick={() => void boot()}
          className="mt-5 rounded-md border border-line bg-surface px-4 py-2 text-sm font-medium text-ink-muted transition-colors hover:bg-raised hover:text-ink"
        >
          Try again
        </button>
      </div>
    </div>
  );
}

function Mark() {
  return (
    <span className="mx-auto grid h-12 w-12 place-items-center rounded-xl bg-accent text-xl font-bold text-ink-inverse">
      T
    </span>
  );
}
