import { useRef, useState } from 'react';
import { useIdentifyUpload } from './api';
import { TrackCard } from './TrackCard';

/**
 * "What song is this?" for a file that is not (or not yet) in the library.
 *
 * This flow matters beyond its obvious use: the sidecar has NO endpoint that
 * lists tracks, so identifying a file is one of the two doors into track
 * browsing — the other being a video's now-playing panel. The result card
 * links straight to every library video sharing the track.
 *
 * The file goes to the sidecar only; nothing is added to Immich. Upload
 * progress is not shown because fetch cannot report it without moving to
 * XMLHttpRequest — for a single local-network file that trade is not worth
 * the code.
 */
export function IdentifyUpload({ onOpenTrack }: { onOpenTrack: (trackKey: string) => void }) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);
  const [fileName, setFileName] = useState<string | null>(null);
  const upload = useIdentifyUpload();

  const identify = (file: File | undefined) => {
    if (!file) return;
    setFileName(file.name);
    upload.mutate(file);
  };

  return (
    <div className="space-y-3">
      {/* A real <button>, so the dropzone is keyboard-operable for free —
          a clickable <div> would need tabIndex, key handlers and a role to
          get back what button already does. */}
      <button
        type="button"
        onClick={() => inputRef.current?.click()}
        onDragOver={(event) => {
          event.preventDefault();
          setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(event) => {
          event.preventDefault();
          setDragging(false);
          identify(event.dataTransfer.files[0]);
        }}
        disabled={upload.isPending}
        className={`w-full rounded-lg border-2 border-dashed px-6 py-10 text-center transition-colors ${
          dragging
            ? 'border-accent bg-accent-muted'
            : 'border-line bg-surface hover:border-ink-faint'
        } disabled:cursor-wait disabled:opacity-60`}
      >
        <p className="text-sm font-medium text-ink">
          {upload.isPending ? `Identifying ${fileName ?? 'file'}…` : 'Drop a video or audio file'}
        </p>
        <p className="mt-1 text-xs text-ink-faint">
          {upload.isPending ? 'Listening for a match' : 'or click to choose one — it is not added to the library'}
        </p>
      </button>

      <input
        ref={inputRef}
        type="file"
        accept="video/*,audio/*"
        hidden
        onChange={(event) => {
          identify(event.target.files?.[0]);
          // Reset so choosing the same file again re-triggers onChange.
          event.target.value = '';
        }}
      />

      {upload.isError && (
        <p className="text-sm text-danger">{upload.error.message}</p>
      )}

      {upload.data && !upload.data.matched && (
        <p className="text-sm text-ink-muted">
          No match for {fileName ?? 'that file'} — the recognition provider does not know it.
        </p>
      )}

      {upload.data?.matched && upload.data.track && (
        <TrackCard
          track={upload.data.track}
          action={
            <button
              type="button"
              onClick={() => {
                if (upload.data?.track) onOpenTrack(upload.data.track.key);
              }}
              className="shrink-0 rounded-md bg-accent px-3 py-2 text-sm font-medium text-ink-inverse transition-colors hover:bg-accent-hover"
            >
              {upload.data.sameTrackCount.toLocaleString()} matching{' '}
              {upload.data.sameTrackCount === 1 ? 'video' : 'videos'}
            </button>
          }
        />
      )}
    </div>
  );
}
