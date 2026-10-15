import { useMutation } from '@tanstack/react-query';
import { apiJson } from '@/shared/api/tessera';

/**
 * Find a face across the library, through Tessera.
 *
 * FaceSearch owns the faces and answers by hash; Tessera hands it the
 * picture, keeps only the matches this account may see, and answers in the
 * ids this app renders. The reverse-face-search sidecar on :2299 - a separate
 * page that read Immich's database and linked to Immich - is no longer used.
 */

/** How close a match is, in FaceSearch's calibrated bands ("The 0.45 Line"). */
export type MatchBand = 'match' | 'review' | 'candidate';

export interface FaceMatch {
  assetId: string;
  /** Embedding distance; smaller is closer. */
  distance: number;
  band: MatchBand;
  /** The folder the matched picture sits in - usually a person's handle. */
  album: string | null;
  /** A name FaceSearch has for this face, if the owner has given one. */
  person: string | null;
}

export interface QueryFace {
  /** [x1, y1, x2, y2] in the query image's pixels. */
  box: [number, number, number, number];
  score: number;
  matches: FaceMatch[];
}

export interface FaceSearchResult {
  imageWidth: number;
  imageHeight: number;
  bands: { match: number; review: number; candidate: number };
  faces: QueryFace[];
}

/** Search with a picture the person supplies. */
export function useFaceSearchUpload() {
  return useMutation({
    mutationFn: (file: File) => {
      const form = new FormData();
      form.append('image', file);
      return apiJson<FaceSearchResult>('/api/faces/search', { method: 'POST', body: form });
    },
  });
}

/** Search with a picture already in the library. */
export function useSimilarFaces() {
  return useMutation({
    mutationFn: (assetId: string) =>
      apiJson<FaceSearchResult>(`/api/assets/${encodeURIComponent(assetId)}/similar-faces`, {
        method: 'POST',
      }),
  });
}
