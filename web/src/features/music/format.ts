/**
 * Pure presentation logic for the music page.
 *
 * Everything here is a plain function of its inputs — no fetching, no dates
 * from the wall clock inside the logic — which is what makes the vitest file
 * next door possible. The components stay thin; the decisions live where they
 * can be tested.
 */

/**
 * The server computes `progress_percent` itself, but it arrives as whatever a
 * float division produced. Rendering trusts nothing wider than 0–100 because
 * a progress bar at 100.04% overflows its track.
 */
export function clampPercent(value: number | null | undefined): number {
  if (value === null || value === undefined || !Number.isFinite(value)) return 0;
  return Math.min(100, Math.max(0, value));
}

/**
 * Milliseconds → "m:ss" (or "h:mm:ss" past the hour), the way every media
 * player writes durations. Null in, null out: the caller decides whether an
 * unknown duration means "hide the badge" — this function does not get to
 * invent "0:00" for it.
 */
export function formatClockMs(ms: number | null | undefined): string | null {
  if (ms === null || ms === undefined || !Number.isFinite(ms) || ms < 0) return null;
  const totalSeconds = Math.round(ms / 1000);
  const seconds = totalSeconds % 60;
  const minutes = Math.floor(totalSeconds / 60) % 60;
  const hours = Math.floor(totalSeconds / 3600);
  const ss = String(seconds).padStart(2, '0');
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${ss}` : `${minutes}:${ss}`;
}

/**
 * One sentence describing what the recognition queue is doing right now.
 *
 * Order matters and encodes precedence: a paused queue is paused even if the
 * identifier routes are down; dead routes matter more than a backlog, because
 * they explain WHY the backlog is not moving.
 */
export interface RecognitionSnapshot {
  paused: boolean;
  identifierAvailable: boolean;
  processing: number;
  pending: number;
}

export function recognitionHeadline(s: RecognitionSnapshot): string {
  if (s.paused) return 'Paused';
  if (!s.identifierAvailable) return 'Recognition routes offline — queue preserved';
  if (s.processing > 0) return `Running — ${s.processing.toLocaleString()} active`;
  if (s.pending > 0) return `Queued — ${s.pending.toLocaleString()} waiting`;
  return 'Idle — nothing left to scan';
}

/** Same idea for the local similar-audio fingerprint index. */
export interface FingerprintSnapshot {
  paused: boolean;
  serviceAvailable: boolean;
  processing: number;
  pending: number;
}

export function fingerprintHeadline(s: FingerprintSnapshot): string {
  if (s.paused) return 'Indexing paused';
  if (!s.serviceAvailable) return 'Fingerprint service offline — queue preserved';
  if (s.processing > 0) return `Indexing — ${s.processing.toLocaleString()} active`;
  if (s.pending > 0) return `Queued — ${s.pending.toLocaleString()} waiting`;
  return 'Index up to date';
}

/**
 * Timestamps from the sidecar are ISO strings or null, and null means the
 * event has never happened for this account — worth saying in words rather
 * than leaving a blank the user reads as a rendering bug.
 */
export function formatWhen(iso: string | null | undefined): string {
  if (!iso) return 'never';
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? 'never' : date.toLocaleString();
}
