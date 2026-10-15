import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import type { AlbumResponseDto } from '@immich/sdk';
import { apiJson } from '@/shared/api/tessera';
import { qk } from '@/shared/api/queries';

/**
 * "Index" — one press, three steps, all Tessera's.
 *
 * Tessera walks just this album's folder so new files are enrolled and in
 * the album, then hands the folder to FaceSearch (faces) and Foundation
 * (images) in the host's spelling, which both understand. Pressing Index is
 * the human start for the face ingest, agreed 2026-09-04; FaceSearch still
 * refuses a second concurrent run and says so in `faces.queuedBehindAnother`.
 * Foundation only REGISTERS - the vectors are GPU work a person starts, and
 * `images.result.run` is the command when anything is pending.
 *
 * Small folders are scanned by the library watch on their own; this button
 * is for the rest of the chain, and for a folder too big to scan unasked.
 */
export interface IndexOutcome {
  folder: string;
  scan: { ok: boolean; seen?: number; added?: number; moved?: number; gone?: number; errors?: number; detail?: string };
  faces: { ok: boolean; requestId?: number; started?: boolean; queuedBehindAnother?: boolean; detail?: string };
  images: { ok: boolean; result?: { pending_vectors?: number; run?: string; registered?: number }; detail?: string };
}

export function useIndexAlbum() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (albumId: string) =>
      apiJson<IndexOutcome>(`/api/albums/${encodeURIComponent(albumId)}/index`, { method: 'POST' }),
    onSuccess: () => client.invalidateQueries({ queryKey: qk.albums.all }),
  });
}

export function IndexAlbum({ album }: { album: AlbumResponseDto }) {
  const index = useIndexAlbum();
  const [open, setOpen] = useState(false);
  const out = index.data;

  return (
    <>
      <button
        type="button"
        onClick={() => {
          setOpen(true);
          index.mutate(album.id);
        }}
        disabled={index.isPending}
        title="Scan this album's folder, then index faces and register images"
        className="inline-flex items-center gap-1.5 rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted transition-colors hover:bg-raised hover:text-ink disabled:cursor-not-allowed disabled:opacity-50"
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden>
          <path d="M21 12a9 9 0 1 1-3-6.7" />
          <path d="M21 3v6h-6" />
        </svg>
        {index.isPending ? 'Indexing…' : 'Index'}
      </button>

      {open && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
          role="dialog"
          aria-modal="true"
          aria-label="Index this album"
          onClick={(event) => {
            if (event.target === event.currentTarget) setOpen(false);
          }}
        >
          <div className="w-full max-w-md rounded-lg border border-line bg-surface p-5 shadow-xl">
            <h2 className="text-base font-semibold text-ink">Index {album.albumName}</h2>
            <p className="mt-1 text-sm text-ink-faint">
              The folder is walked for new files, faces go to FaceSearch and images are registered
              with Foundation. Nothing is removed.
            </p>

            <ul className="mt-4 space-y-1 text-sm">
              <Step
                label="Library sees the files"
                state={index.isPending ? 'running' : out ? (out.scan.ok ? 'done' : 'error') : 'idle'}
                detail={out?.scan.ok ? `${out.scan.added ?? 0} new · ${out.scan.seen ?? 0} seen` : out?.scan.detail}
              />
              <Step
                label="Faces indexed"
                state={index.isPending ? 'running' : out ? (out.faces.ok ? 'done' : 'error') : 'idle'}
                detail={out?.faces.queuedBehindAnother ? 'queued behind another ingest' : out?.faces.detail}
              />
              <Step
                label="Registered for SigLIP2 / DINOv3"
                state={index.isPending ? 'running' : out ? (out.images.ok ? 'done' : 'error') : 'idle'}
                detail={out?.images.ok ? `${out.images.result?.registered ?? 0} registered` : out?.images.detail}
              />
            </ul>

            {(out?.images.result?.pending_vectors ?? 0) > 0 && out?.images.result?.run && (
              <p className="mt-3 break-all rounded bg-ground p-2 font-mono text-[11px] text-ink-faint">
                {out.images.result.run}
              </p>
            )}
            {index.isError && (
              <p className="mt-2 text-xs text-warn">{(index.error as Error).message}</p>
            )}

            <div className="mt-5 flex justify-end">
              <button
                type="button"
                onClick={() => setOpen(false)}
                className="rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted hover:text-ink"
              >
                Close
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

function Step({ label, state, detail }: { label: string; state: 'idle' | 'running' | 'done' | 'error'; detail?: string | undefined }) {
  const mark = state === 'done' ? '✓' : state === 'error' ? '✕' : state === 'running' ? '…' : '·';
  const tone = state === 'done' ? 'text-ink-muted' : state === 'error' ? 'text-warn' : 'text-ink-faint';
  return (
    <li className={tone}>
      <span aria-hidden>{mark}</span> {label}
      {detail && <span className="ml-1 text-xs">— {detail}</span>}
    </li>
  );
}
