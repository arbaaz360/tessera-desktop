import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  QueueCommand,
  getConfig,
  getQueues,
  isHttpError,
  runQueueCommandLegacy,
  updateQueue,
  updateConfig,
  type QueueName,
} from '@immich/sdk';
import { apiJson } from '@/shared/api/tessera';

/**
 * Server state for the admin feature.
 *
 * These hooks live here rather than in `shared/api/queries.ts` because that
 * file holds state shared ACROSS features; nothing outside `features/admin`
 * reads queues, accounts, roots or drift. Feature-local hooks keep the
 * shared file honest about what is actually shared.
 *
 * A note on the job API, because v3 split it in a way that is easy to misread.
 * v2.4.0 introduced a modern `/queues` controller (list, pause/resume, empty,
 * inspect jobs) and DEPRECATED — but kept — the old `/jobs` controller. Two
 * commands still exist only on the deprecated side:
 *
 *   - "start a run"  — PUT /jobs/:name {command:'start', force}
 *   - "clear failed" — PUT /jobs/:name {command:'clear-failed'}
 *
 * The modern DELETE /queues/:name/jobs is NOT a substitute for clear-failed:
 * it drains the queue's waiting work, and `failed: true` merely clears failed
 * jobs IN ADDITION. Clearing failures mid-backfill must not throw away the
 * 200k jobs still queued, so we call the legacy command and leave the modern
 * empty endpoint deliberately unwired. All of this was verified against the
 * live v3.1.0 sandbox, not just read from the source.
 */

/**
 * Query keys, feature-local, same factory discipline as the shared `qk`.
 * The 'admin' prefix keeps these from ever colliding with browsing keys.
 */
export const ak = {
  all: ['admin'] as const,
  queues: ['admin', 'queues'] as const,
  accounts: ['admin', 'accounts'] as const,
  roots: ['admin', 'roots'] as const,
  scans: ['admin', 'scans'] as const,
  drift: ['admin', 'drift'] as const,
  duplicates: (scope: 'within' | 'across') => ['admin', 'duplicates', scope] as const,
  systemConfig: ['admin', 'system-config'] as const,
  folders: ['admin', 'folders'] as const,
} as const;

/**
 * Five seconds: fast enough that a draining queue visibly moves, slow enough
 * that nineteen queues polling does not bother the server. TanStack Query's
 * default `refetchIntervalInBackground: false` pauses the poll when the tab
 * is hidden, which is exactly the "only while the page is visible" behaviour
 * we want — no visibility listener needed.
 */
const QUEUE_POLL_MS = 5_000;

export function useQueues() {
  return useQuery({
    queryKey: ak.queues,
    queryFn: () => getQueues(),
    refetchInterval: QUEUE_POLL_MS,
  });
}

/**
 * All three queue controls for one queue, as one hook.
 *
 * Returned as separate mutations rather than one keyed by command, so each
 * button gets its own pending/error state — "Pause is in flight" must not
 * disable the Start button or claim its error slot.
 *
 * Every success invalidates the queue list instead of waiting out the 5s
 * poll: the person just clicked a button, and the next paint should show the
 * consequence.
 */
export function useQueueActions(name: QueueName) {
  const client = useQueryClient();
  const invalidate = () => client.invalidateQueries({ queryKey: ak.queues });

  const start = useMutation({
    mutationFn: (force: boolean) =>
      runQueueCommandLegacy({ name, queueCommandDto: { command: QueueCommand.Start, force } }),
    onSuccess: invalidate,
  });

  const setPaused = useMutation({
    mutationFn: (isPaused: boolean) => updateQueue({ name, queueUpdateDto: { isPaused } }),
    onSuccess: invalidate,
  });

  const clearFailed = useMutation({
    mutationFn: () =>
      runQueueCommandLegacy({ name, queueCommandDto: { command: QueueCommand.ClearFailed } }),
    onSuccess: invalidate,
  });

  return { start, setPaused, clearFailed };
}

/* ------------------------------------------------ Tessera's own admin surface */

export interface AdminAccount {
  accountId: number;
  email: string;
  displayName: string;
  isAdmin: boolean;
  kind: 'personal' | 'corpus';
  isCorpus: boolean;
}

export interface ScanRoot {
  id: number;
  label: string;
  abs_path: string;
  enabled: boolean;
  account: string;
  live_files: number;
}

export interface ScanRun {
  run_id: number;
  label: string;
  started_at: string;
  finished_at: string | null;
  files_seen: number;
  added: number;
  moved: number;
  unchanged: number;
  marked_gone: number;
  errors: number;
  error_sample: string | null;
  cancelled: boolean;
}

export interface Drift {
  content_rows: number;
  live_locations: number;
  gone_locations: number;
  trashed: number;
  timeline_rows: number;
  missing_thumbnails: number;
  orphan_thumbnails: number;
  work_pending: number;
  work_failed: number;
  duplicate_sets_in_account: number;
  duplicate_sets_across_accounts: number;
}

export interface WithinDuplicate {
  account_id: number;
  content_id: number;
  copies: number;
  paths: string[];
}

export interface AcrossDuplicate {
  content_id: number;
  accounts: number[];
  copies: number;
}

export function useAccounts() {
  return useQuery({ queryKey: ak.accounts, queryFn: () => apiJson<AdminAccount[]>('/api/admin/accounts') });
}

export function useRoots() {
  return useQuery({ queryKey: ak.roots, queryFn: () => apiJson<ScanRoot[]>('/api/admin/roots') });
}

export function useScans() {
  return useQuery({
    queryKey: ak.scans,
    queryFn: () => apiJson<ScanRun[]>('/api/admin/scans'),
    refetchInterval: QUEUE_POLL_MS * 2,
  });
}

/** Walk a whole root now. Tessera refuses (409) while another scan runs. */
export function useScanRoot() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (rootId: number) =>
      apiJson<{ started: boolean }>(`/api/scan/roots/${rootId}/scan`, { method: 'POST' }),
    onSuccess: () => client.invalidateQueries({ queryKey: ak.scans }),
  });
}

/** Counted live, so it is slow-ish; a minute of staleness is fine for a health page. */
export function useDrift() {
  return useQuery({ queryKey: ak.drift, queryFn: () => apiJson<Drift>('/api/admin/drift'), staleTime: 60_000 });
}

export function useWithinDuplicates() {
  return useQuery({
    queryKey: ak.duplicates('within'),
    queryFn: () => apiJson<WithinDuplicate[]>('/api/admin/duplicates/within?limit=50'),
    staleTime: 60_000,
  });
}

export function useAcrossDuplicates() {
  return useQuery({
    queryKey: ak.duplicates('across'),
    queryFn: () => apiJson<AcrossDuplicate[]>('/api/admin/duplicates/across?limit=100'),
    staleTime: 60_000,
  });
}

/**
 * Folders the library watch has noticed and not yet scanned: the ones still
 * settling (a copy in progress), and the ones too big to scan unasked, which
 * wait here for a person. Tessera's own shape (`/api/scan/folders`).
 */
export interface WaitingFolder {
  rootId: number;
  rootLabel: string;
  accountId: number;
  folder: string;
  entries: number | null;
  firstSeen: string;
  changedAt: string;
  waitingForYou: boolean;
}

export interface WaitingFolders {
  autoScanMaxEntries: number;
  settleSeconds: number;
  folders: WaitingFolder[];
}

export function useWaitingFolders() {
  return useQuery({
    queryKey: ak.folders,
    queryFn: () => apiJson<WaitingFolders>('/api/scan/folders'),
    refetchInterval: QUEUE_POLL_MS * 2,
  });
}

/** Start a scoped scan of one folder - the human start the watch waits for. */
export function useScanFolder() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ rootId, folder }: { rootId: number; folder: string }) =>
      apiJson<{ started: boolean }>(
        `/api/scan/roots/${rootId}/scan?folder=${encodeURIComponent(folder)}`,
        { method: 'POST' },
      ),
    onSuccess: () => client.invalidateQueries({ queryKey: ak.folders }),
  });
}

export function useSystemConfig() {
  return useQuery({
    queryKey: ak.systemConfig,
    queryFn: () => getConfig(),
    // Config changes out-of-band (API calls, config file) and rarely. Five
    // minutes of staleness beats refetching the whole config tree per visit.
    staleTime: 5 * 60_000,
  });
}

/**
 * Change one queue's worker concurrency.
 *
 * This is the ONLY throttle Immich offers for background work: how many jobs of
 * a given kind run at once. There is no rate limit and no "gentle mode" — a
 * queue at concurrency 1 still runs flat out, it just runs one at a time. So the
 * practical controls for a GPU-heavy backfill are this number and pause/resume,
 * and it is worth saying so in the UI rather than leaving someone hunting for a
 * slider that does not exist.
 *
 * Reads the whole config and writes it back with one field changed, because
 * `updateConfig` replaces the document — a partial PUT would blank every other
 * setting. Reading immediately before writing keeps the window for clobbering a
 * concurrent change as small as this API allows.
 */
export function useSetQueueConcurrency() {
  const client = useQueryClient();

  return useMutation({
    mutationFn: async ({ queue, concurrency }: { queue: string; concurrency: number }) => {
      const current = await getConfig();
      return updateConfig({
        systemConfigDto: {
          ...current,
          job: {
            ...current.job,
            [queue]: { concurrency },
          },
        } as Parameters<typeof updateConfig>[0]['systemConfigDto'],
      });
    },
    onSuccess: () => client.invalidateQueries({ queryKey: ak.systemConfig }),
  });
}

/**
 * One place that knows how to read an error.
 *
 * The SDK throws `HttpError` whose `data` is the NestJS exception body; its
 * `message` field is what the server actually wanted to say ("The
 * BackgroundTask queue cannot be paused"), which beats the generic
 * "Request failed with status 400" on the error object itself.
 */
export function apiErrorMessage(error: unknown): string {
  if (isHttpError(error) && typeof error.data?.message === 'string') {
    return error.data.message;
  }
  return error instanceof Error ? error.message : 'Something went wrong.';
}
