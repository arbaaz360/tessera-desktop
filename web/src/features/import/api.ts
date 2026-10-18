import type { QueryClient } from '@tanstack/react-query';
import { apiJson } from '@/shared/api/tessera';
import { qk } from '@/shared/api/queries';
import type { ImportCounts, ImportJob } from '@/shared/api/types';

/**
 * Adding pictures, as the core answers it (contract §4, imports).
 *
 *   POST /api/import {paths, urls, albumId}  → 202, a job, every item pending
 *   GET  /api/import/{id}                    → the job as it stands
 *   POST /api/import/upload?name=&albumId=   → bytes the webview handed over
 *
 * The core decides what each item means: a folder is indexed where it is, a
 * file inside a library folder too, anything else is copied into the Tessera
 * folder, and bytes the library already holds are never copied again.
 */

export interface ImportTarget {
  albumId: string;
  albumName: string;
}

export function startImport(body: { paths?: string[]; urls?: string[]; albumId?: string }): Promise<ImportJob> {
  return apiJson<ImportJob>('/api/import', { method: 'POST', body: JSON.stringify(body) });
}

export function getImport(id: number): Promise<ImportJob> {
  return apiJson<ImportJob>(`/api/import/${id}`);
}

/** One file's bytes, for a drop or pick that came without a path (a browser). */
export function uploadFile(file: File, albumId?: string): Promise<ImportJob> {
  const query = new URLSearchParams({ name: file.name });
  if (albumId) query.set('albumId', albumId);
  return apiJson<ImportJob>(`/api/import/upload?${query.toString()}`, {
    method: 'POST',
    body: file,
    headers: { 'content-type': 'application/octet-stream' },
  });
}

/**
 * What an import changes: the grid and every album's grid (new pictures,
 * new members), the album list (counts, covers) and the library's numbers.
 */
export function invalidateAfterImport(client: QueryClient): Promise<unknown> {
  return Promise.all([
    client.invalidateQueries({ queryKey: qk.timeline.all }),
    client.invalidateQueries({ queryKey: qk.albums.all }),
    client.invalidateQueries({ queryKey: qk.library.all }),
  ]);
}

/** "2 added · 1 already there · 3 copied · 1 skipped · 1 folder", leaving out the zeros. */
export function summarizeCounts(counts: ImportCounts): string {
  const parts: string[] = [];
  if (counts.added) parts.push(`${counts.added.toLocaleString()} added`);
  if (counts.copied) parts.push(`${counts.copied.toLocaleString()} copied in`);
  if (counts.already) parts.push(`${counts.already.toLocaleString()} already there`);
  if (counts.folders) parts.push(`${counts.folders.toLocaleString()} ${counts.folders === 1 ? 'folder' : 'folders'}`);
  if (counts.skipped) parts.push(`${counts.skipped.toLocaleString()} skipped`);
  return parts.length ? parts.join(' · ') : 'nothing yet';
}

export const NO_COUNTS: ImportCounts = { added: 0, already: 0, copied: 0, skipped: 0, folders: 0 };

export function addCounts(a: ImportCounts, b: ImportCounts): ImportCounts {
  return {
    added: a.added + b.added,
    already: a.already + b.already,
    copied: a.copied + b.copied,
    skipped: a.skipped + b.skipped,
    folders: a.folders + b.folders,
  };
}

/**
 * What the file picker offers and the drop accepts as pictures and videos:
 * the extensions the core's scanner enumerates.
 */
export const MEDIA_EXTENSIONS = [
  'jpg', 'jpeg', 'png', 'webp', 'gif', 'bmp', 'tif', 'tiff', 'heic', 'heif', 'avif', 'jxl',
  'dng', 'cr2', 'cr3', 'nef', 'arw', 'orf', 'rw2', 'raf',
  'mp4', 'mov', 'webm', 'mkv', 'avi', 'm4v', 'wmv', 'vob', '3gp', 'mts', 'm2ts', 'mpg', 'mpeg', 'flv',
  'insp', 'insv',
] as const;

/** For `<input type="file" accept>`. */
export const MEDIA_ACCEPT = ['image/*', 'video/*', ...MEDIA_EXTENSIONS.map((ext) => `.${ext}`)].join(',');
