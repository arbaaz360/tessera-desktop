import type { SearchStatus } from '@/shared/api/types';

/**
 * Where the search model is, as one word the UI can switch on.
 *
 * The status has no "loading" flag, but it does not need one: while search
 * is on, the core's embedding worker keeps asking for the model, so a model
 * that is neither ready nor downloading nor failed is being checked and
 * loaded. On this machine that takes about 50 seconds after every start
 * (the files are 755 MB). Calling that state "not downloaded" sent people to
 * a Download button for a model already on disk.
 *
 *   off          search is switched off
 *   downloading  bytes are arriving
 *   failed       the last attempt failed; `error` says why
 *   loading      on disk or about to be fetched, not ready yet
 *   indexing     ready, some pictures still without a vector
 *   ready        ready, every picture has a vector
 */
export type ModelState = 'off' | 'downloading' | 'failed' | 'loading' | 'indexing' | 'ready';

export function modelState(status: SearchStatus): ModelState {
  if (!status.enabled) return 'off';
  if (status.ready) return status.embedded < status.total ? 'indexing' : 'ready';
  if (status.downloading != null) return 'downloading';
  if (status.error != null) return 'failed';
  return 'loading';
}

export const MODEL_STATE_LABEL: Record<ModelState, string> = {
  off: 'off',
  downloading: 'downloading',
  failed: 'download failed',
  loading: 'loading',
  indexing: 'indexing',
  ready: 'ready',
};

/** The size of the one download, for the sentence that asks for it. */
export const MODEL_DOWNLOAD_SIZE = 'About 755 MB';
