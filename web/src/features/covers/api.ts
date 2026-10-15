import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { qk } from '@/shared/api/queries';
import { apiJson } from '@/shared/api/tessera';

/**
 * Album covers, through Tessera.
 *
 * Tessera owns the album and the choice; a worker behind Tessera's contract
 * (Tessera/docs/contracts/cover-picker.md) does the ranking. This app talks to
 * neither the worker nor FaceSearch: it asks Tessera, same-origin, with the
 * session it already has, and gets back the ids it already knows how to
 * render. The previous sidecar on :3111 wrote straight into Immich's database
 * and is no longer in this path.
 */

export interface CoverCandidate {
  /** 1-based position in the ranking. The current cover, if it ranks, is #1. */
  rank: number;
  assetId: string;
  /** For alt text and tooltips - the name of one live copy of the file. */
  fileName: string;
  /** The face that anchored this candidate's score. */
  faceId: string;
  /** 0..1 */
  score: number;
  /** Reasons a person might disagree with the score: face_too_small, too_close_for_cover, many_faces. */
  flags: string[];
  metrics: {
    faceHeightRatio: number;
    belowFaceRatio: number;
    centerXError: number;
    centerY: number;
    facesInImage: number;
  };
}

export interface CoverCandidatesResponse {
  albumId: string;
  albumName: string;
  /** null when the album has no chosen cover (it is showing a stable member). */
  currentCoverAssetId: string | null;
  /** Images in the album that had face evidence to rank on. */
  considered: number;
  candidates: CoverCandidate[];
}

export const coverKeys = {
  all: ['covers'] as const,
  workers: () => [...coverKeys.all, 'workers'] as const,
  // `top` is part of the key: the hover strip's 5 and the modal's 40 are
  // different responses and must not share a cache entry.
  candidates: (albumId: string, top: number) =>
    [...coverKeys.all, 'candidates', albumId, top] as const,
};

/** Candidates for the hover strip — just the obvious winners. */
export const HOVER_CANDIDATES = 5;

/** Candidates for the full picker: the point of opening it is to see past the top few. */
export const MODAL_CANDIDATES = 40;

/**
 * Is the ranking worker up?
 *
 * Tessera reports every worker it can reach at /api/workers. Every "pick
 * cover" button mounts this; identical keys mean one probe a minute however
 * many album cards are on screen. "Down" is a state we render (a quietly
 * disabled button), not an error to retry.
 */
export function useCoverPickerHealth() {
  return useQuery({
    queryKey: coverKeys.workers(),
    queryFn: async (): Promise<boolean> => {
      try {
        const workers = await apiJson<{ name: string; ok: boolean }[]>('/api/workers');
        return workers.find((w) => w.name === 'cover-picker')?.ok === true;
      } catch {
        return false;
      }
    },
    staleTime: 60_000,
    retry: false,
  });
}

/** Ranked cover candidates for one album. */
export function useCoverCandidates(albumId: string, enabled: boolean, top = HOVER_CANDIDATES) {
  return useQuery({
    queryKey: coverKeys.candidates(albumId, top),
    queryFn: () =>
      apiJson<CoverCandidatesResponse>(
        `/api/albums/${encodeURIComponent(albumId)}/cover-candidates?top=${top}`,
      ),
    enabled,
    staleTime: 60_000,
    retry: false,
  });
}

/**
 * Choose an album's cover.
 *
 * The choice is final until the next choice. Nothing automatic overwrites
 * it: Tessera has no cover job, the ranking is only ever a suggestion.
 */
export function useSetCover(albumId: string) {
  const client = useQueryClient();

  return useMutation({
    mutationFn: (assetId: string) =>
      apiJson<unknown>(`/api/albums/${encodeURIComponent(albumId)}/cover`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ assetId }),
      }),
    onSuccess: () => {
      // The new cover must show on the albums grid and as "current" inside
      // the picker, at every depth this album's candidates were fetched.
      void client.invalidateQueries({ queryKey: qk.albums.all });
      void client.invalidateQueries({ queryKey: [...coverKeys.all, 'candidates', albumId] });
    },
  });
}
