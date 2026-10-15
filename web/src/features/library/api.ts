import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiJson } from '@/shared/api/tessera';
import { qk } from '@/shared/api/queries';
import type {
  DuplicateSet,
  Library,
  LibraryRoot,
  Licence,
  SearchStatus,
  Settings,
  SettingsPatch,
} from '@/shared/api/types';

/**
 * The core's own routes (contract §4): the library, search status, settings
 * and the licence. Used by onboarding, the Library page, Settings and the
 * search page's "model not ready" state, so they live in one feature-level
 * module rather than being copied into each.
 *
 * Polling is deliberate and bounded. Scans, thumbnails and embeddings are
 * background work the person watches; the Library page repolls while any of
 * it is moving and slows down when it is not. TanStack Query pauses polls
 * while the window is hidden, which is the "only while someone is looking"
 * rule for free.
 */

const BUSY_POLL_MS = 2_000;
const IDLE_POLL_MS = 15_000;

/** Is anything in the library still being worked on? */
export function libraryIsBusy(library: Library): boolean {
  return (
    library.roots.some((root) => root.scanning) ||
    library.thumbnails.pending > 0 ||
    (library.embeddings.pending > 0 && library.embeddings.ready)
  );
}

export function useLibrary(options: { poll?: boolean } = {}) {
  const poll = options.poll ?? true;
  return useQuery({
    queryKey: qk.library.summary(),
    queryFn: () => apiJson<Library>('/api/library'),
    staleTime: 1_000,
    refetchInterval: poll
      ? (query) => (query.state.data && libraryIsBusy(query.state.data) ? BUSY_POLL_MS : IDLE_POLL_MS)
      : false,
  });
}

export function useAddRoot() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (path: string) =>
      apiJson<LibraryRoot>('/api/library/roots', { method: 'POST', body: JSON.stringify({ path }) }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.library.all }),
  });
}

/** Removes the root's location rows. Files are untouched (contract §4). */
export function useRemoveRoot() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (rootId: number) => apiJson<void>(`/api/library/roots/${rootId}`, { method: 'DELETE' }),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: qk.library.all });
      // The grid and the albums just lost whatever lived under that root.
      void client.invalidateQueries({ queryKey: qk.timeline.all });
      void client.invalidateQueries({ queryKey: qk.albums.all });
    },
  });
}

/** Walk a root now. The core answers 409 while a scan of it is running. */
export function useScanRoot() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (rootId: number) =>
      apiJson<{ runId: number }>(`/api/library/roots/${rootId}/scan`, { method: 'POST' }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.library.all }),
  });
}

export function useDuplicates(limit = 200) {
  return useQuery({
    queryKey: [...qk.library.duplicates(), limit] as const,
    queryFn: () => apiJson<DuplicateSet[]>(`/api/library/duplicates?limit=${limit}`),
    staleTime: 30_000,
  });
}

// ------------------------------------------------------------------ search

/**
 * Is there anything to watch? A model that is absent counts: the person is
 * about to press Download, or the core is about to start one itself, and a
 * slow poll would show "not downloaded" for a quarter of a minute after
 * either. Only "ready and every picture embedded" is idle.
 */
export function searchIsBusy(status: SearchStatus): boolean {
  if (!status.enabled) return false;
  return !status.ready || status.downloading !== null || status.embedded < status.total;
}

export function useSearchStatus(options: { poll?: boolean } = {}) {
  const poll = options.poll ?? true;
  return useQuery({
    queryKey: qk.search.status(),
    queryFn: () => apiJson<SearchStatus>('/api/search/status'),
    staleTime: 1_000,
    refetchInterval: poll
      ? (query) => (query.state.data && searchIsBusy(query.state.data) ? BUSY_POLL_MS : IDLE_POLL_MS)
      : false,
  });
}

/** `202`, idempotent: asking twice does not download twice. */
export function useStartModelDownload() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => apiJson<void>('/api/search/model/download', { method: 'POST' }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.search.status() }),
  });
}

export function useSetSearchEnabled() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (enabled: boolean) =>
      apiJson<void>('/api/search/settings', { method: 'PUT', body: JSON.stringify({ enabled }) }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.search.all }),
  });
}

// ---------------------------------------------------------------- settings

export function useSettings() {
  return useQuery({
    queryKey: qk.settings,
    queryFn: () => apiJson<Settings>('/api/settings'),
    staleTime: 60_000,
  });
}

export function useUpdateSettings() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (patch: SettingsPatch) =>
      apiJson<Settings>('/api/settings', { method: 'PUT', body: JSON.stringify(patch) }),
    onSuccess: (settings) => client.setQueryData(qk.settings, settings),
  });
}

// ----------------------------------------------------------------- licence

export function useLicence() {
  return useQuery({
    queryKey: qk.licence,
    queryFn: () => apiJson<Licence>('/api/licence'),
    staleTime: 60_000,
  });
}
