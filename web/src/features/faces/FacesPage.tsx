import { useEffect, useRef, useState } from 'react';
import { Link, useSearch } from '@tanstack/react-router';
import { assetThumbnailUrl } from '@/shared/api/client';
import { useFaceSearchUpload, useSimilarFaces, type FaceSearchResult, type MatchBand, type QueryFace } from './api';

/**
 * Find a face across the library.
 *
 * Two ways in: drop a picture, or arrive from the viewer with `?assetId=`
 * and the picture already in the library. Either way the answer is the same
 * shape - each face found in the query picture, and the library pictures
 * that face resembles, nearest first, with FaceSearch's own confidence band
 * on each: MATCH is the same person, REVIEW is worth a look, CANDIDATE is a
 * long shot. The bands are calibrated against the owner's judgement, so they
 * are read as-is rather than re-thresholded here.
 */
export function FacesPage() {
  const { assetId } = useSearch({ from: '/authed/faces' });
  const upload = useFaceSearchUpload();
  const similar = useSimilarFaces();
  const inputRef = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);
  const [fileName, setFileName] = useState<string | null>(null);

  // Arriving from the viewer: search that picture, once.
  useEffect(() => {
    if (assetId) similar.mutate(assetId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [assetId]);

  const search = (file: File | undefined) => {
    if (!file) return;
    setFileName(file.name);
    upload.mutate(file);
  };

  const pending = upload.isPending || similar.isPending;
  const error = upload.error ?? similar.error;
  const result: FaceSearchResult | undefined = upload.data ?? similar.data;

  return (
    <div className="scrollbar-subtle h-full overflow-y-auto">
      <div className="mx-auto max-w-5xl space-y-6 px-6 py-6">
        <header>
          <h1 className="text-lg font-semibold tracking-tight text-ink">Find a face</h1>
          <p className="mt-0.5 text-sm text-ink-muted">
            Every face in the picture, and the library pictures it resembles.
          </p>
        </header>

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
            search(event.dataTransfer.files[0]);
          }}
          disabled={pending}
          className={`w-full rounded-lg border-2 border-dashed px-6 py-8 text-center transition-colors ${
            dragging ? 'border-accent bg-accent-muted' : 'border-line bg-surface hover:border-ink-faint'
          } disabled:cursor-wait disabled:opacity-60`}
        >
          <p className="text-sm font-medium text-ink">
            {pending ? `Looking for ${fileName ?? 'this face'}…` : 'Drop a picture'}
          </p>
          <p className="mt-1 text-xs text-ink-faint">
            {pending ? 'Detecting faces, then searching the library' : 'or click to choose one — it is not added to the library'}
          </p>
        </button>
        <input
          ref={inputRef}
          type="file"
          accept="image/*"
          hidden
          onChange={(event) => {
            search(event.target.files?.[0]);
            event.target.value = '';
          }}
        />

        {error && <p className="text-sm text-danger">{error.message}</p>}

        {result && result.faces.length === 0 && (
          <p className="text-sm text-ink-muted">No face was found in that picture.</p>
        )}

        {result?.faces.map((face, i) => (
          <FaceResults key={i} index={i} face={face} total={result.faces.length} />
        ))}
      </div>
    </div>
  );
}

function FaceResults({ face, index, total }: { face: QueryFace; index: number; total: number }) {
  const byBand = (band: MatchBand) => face.matches.filter((m) => m.band === band);
  const sections: [MatchBand, string][] = [
    ['match', 'Same person'],
    ['review', 'Worth a look'],
    ['candidate', 'Long shots'],
  ];
  return (
    <section className="space-y-3">
      <h2 className="text-base font-semibold tracking-tight text-ink">
        {total > 1 ? `Face ${index + 1} of ${total}` : 'Matches'}
        <span className="ml-2 text-xs font-normal text-ink-faint">
          {face.matches.length === 0 ? 'nothing close' : `${face.matches.length} nearest`}
        </span>
      </h2>
      {sections.map(([band, label]) => {
        const list = byBand(band);
        if (list.length === 0) return null;
        return (
          <div key={band}>
            <p className="mb-2 text-xs uppercase tracking-wide text-ink-faint">
              {label} · {list.length}
            </p>
            <ul className="grid gap-2" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(120px, 1fr))' }}>
              {list.map((m) => (
                <li key={m.assetId}>
                  <Link
                    to="/photos/$assetId"
                    params={{ assetId: m.assetId }}
                    className="block overflow-hidden rounded-md border border-line bg-surface hover:border-accent"
                    title={[m.person, m.album, `distance ${m.distance.toFixed(3)}`].filter(Boolean).join(' · ')}
                  >
                    <img
                      src={assetThumbnailUrl(m.assetId)}
                      alt=""
                      loading="lazy"
                      className="aspect-square w-full object-cover"
                    />
                    <p className="truncate px-1.5 py-1 text-[11px] text-ink-muted">
                      {m.person ?? m.album ?? ''}
                    </p>
                  </Link>
                </li>
              ))}
            </ul>
          </div>
        );
      })}
    </section>
  );
}
