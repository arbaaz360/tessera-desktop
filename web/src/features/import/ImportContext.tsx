import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { apiErrorMessage } from '@/shared/api/tessera';
import type { ImportItem, ImportJob } from '@/shared/api/types';
import {
  NO_COUNTS,
  addCounts,
  getImport,
  invalidateAfterImport,
  startImport,
  uploadFile,
  type ImportTarget,
} from './api';

/**
 * Imports under way, for the whole app: whatever started one (a drop, the
 * "Add pictures" button, a folder) hands it here, the toasts read it.
 *
 * A job from the core is polled until it finishes. While it runs, the grid,
 * the albums and the library numbers are refreshed every couple of seconds,
 * so pictures appear as they are added; when it finishes they are refreshed
 * once more. Bytes (a browser drop, a browser file pick) go up one file at a
 * time and are shown as one job of the same shape.
 */

export interface ImportEntry {
  key: string;
  target: ImportTarget | null;
  /** The job as it stands; null until the core has answered. */
  job: ImportJob | null;
  /** The request itself failed. */
  error: string | null;
  /** A line with no job behind it ("this drop carried no file"). */
  note: string | null;
}

interface ImportApi {
  entries: ImportEntry[];
  importPaths: (paths: string[], target: ImportTarget | null) => Promise<void>;
  importFiles: (files: File[], target: ImportTarget | null) => Promise<void>;
  importUrls: (urls: string[], target: ImportTarget | null) => Promise<void>;
  notify: (note: string) => void;
  dismiss: (key: string) => void;
}

const ImportContext = createContext<ImportApi | null>(null);

const POLL_MS = 500;
/** How often the grid is refreshed while an import runs. */
const REFRESH_MS = 2_000;

let sequence = 0;
const nextKey = () => `import-${++sequence}`;

export function ImportProvider({ children }: { children: ReactNode }) {
  const client = useQueryClient();
  const [entries, setEntries] = useState<ImportEntry[]>([]);
  const lastRefresh = useRef(0);
  /** Items done per entry at the last poll, to refresh only when something moved. */
  const seen = useRef(new Map<string, number>());

  const update = useCallback((key: string, change: Partial<ImportEntry>) => {
    setEntries((current) => current.map((entry) => (entry.key === key ? { ...entry, ...change } : entry)));
  }, []);

  const refresh = useCallback(
    (force: boolean) => {
      const now = Date.now();
      if (!force && now - lastRefresh.current < REFRESH_MS) return;
      lastRefresh.current = now;
      void invalidateAfterImport(client);
    },
    [client],
  );

  const begin = useCallback((target: ImportTarget | null, note: string | null = null): string => {
    const key = nextKey();
    setEntries((current) => [...current, { key, target, job: null, error: null, note }]);
    return key;
  }, []);

  const startJob = useCallback(
    async (body: { paths?: string[]; urls?: string[] }, target: ImportTarget | null) => {
      const key = begin(target);
      try {
        const job = await startImport({ ...body, ...(target ? { albumId: target.albumId } : {}) });
        update(key, { job });
      } catch (error) {
        update(key, { error: apiErrorMessage(error) });
      }
    },
    [begin, update],
  );

  const importPaths = useCallback(
    (paths: string[], target: ImportTarget | null) => startJob({ paths }, target),
    [startJob],
  );

  const importUrls = useCallback(
    (urls: string[], target: ImportTarget | null) => startJob({ urls }, target),
    [startJob],
  );

  const importFiles = useCallback(
    async (files: File[], target: ImportTarget | null) => {
      if (files.length === 0) return;
      const key = begin(target);
      const items: ImportItem[] = files.map((file) => ({ source: file.name, kind: 'upload', outcome: 'pending' }));
      let job: ImportJob = {
        id: -1,
        ...(target ? { albumId: target.albumId } : {}),
        state: 'running',
        startedAt: new Date().toISOString(),
        total: files.length,
        done: 0,
        counts: NO_COUNTS,
        items,
      };
      update(key, { job });

      for (const [index, file] of files.entries()) {
        let item: ImportItem;
        let counts = NO_COUNTS;
        try {
          const answer = await uploadFile(file, target?.albumId);
          item = answer.items[0] ?? { source: file.name, kind: 'upload', outcome: 'skipped', reason: 'no answer' };
          counts = answer.counts;
        } catch (error) {
          item = { source: file.name, kind: 'upload', outcome: 'skipped', reason: apiErrorMessage(error) };
          counts = { ...NO_COUNTS, skipped: 1 };
        }
        job = {
          ...job,
          done: index + 1,
          counts: addCounts(job.counts, counts),
          items: job.items.map((existing, i) => (i === index ? item : existing)),
        };
        update(key, { job });
        refresh(false);
      }

      update(key, { job: { ...job, state: 'done', finishedAt: new Date().toISOString() } });
      refresh(true);
    },
    [begin, update, refresh],
  );

  const notify = useCallback((note: string) => void begin(null, note), [begin]);

  const dismiss = useCallback((key: string) => {
    setEntries((current) => current.filter((entry) => entry.key !== key));
  }, []);

  // Poll the core's running jobs. One timer for all of them.
  const running = entries.filter((entry) => entry.job && entry.job.id > 0 && entry.job.state === 'running');
  const runningIds = running.map((entry) => `${entry.key}:${entry.job!.id}`).join(',');
  useEffect(() => {
    if (!runningIds) return;
    const timer = setInterval(() => {
      for (const pair of runningIds.split(',')) {
        const [key, id] = pair.split(':');
        void getImport(Number(id))
          .then((job) => {
            const before = seen.current.get(key!);
            seen.current.set(key!, job.done);
            update(key!, { job });
            if (job.state !== 'running') refresh(true);
            else if (before !== job.done) refresh(false);
          })
          .catch((error: unknown) => update(key!, { error: apiErrorMessage(error) }));
      }
    }, POLL_MS);
    return () => clearInterval(timer);
  }, [runningIds, refresh, update]);

  const api = useMemo<ImportApi>(
    () => ({ entries, importPaths, importFiles, importUrls, notify, dismiss }),
    [entries, importPaths, importFiles, importUrls, notify, dismiss],
  );

  return <ImportContext.Provider value={api}>{children}</ImportContext.Provider>;
}

export function useImports(): ImportApi {
  const api = useContext(ImportContext);
  if (!api) throw new Error('useImports needs an <ImportProvider> above it (AppBoot mounts one).');
  return api;
}

/** The same, or null where no provider is mounted (a page rendered on its own in a test). */
export function useImportsIfMounted(): ImportApi | null {
  return useContext(ImportContext);
}
