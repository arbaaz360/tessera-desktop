/**
 * Formatting helpers shared across features, pure and unit-tested.
 *
 * These began life in `features/admin`. They moved here the moment the
 * viewer's info panel needed byte formatting too — a helper with two unrelated
 * callers is shared code, and reaching across feature folders to import one is
 * how a codebase quietly grows dependencies nobody intended.
 *
 * Both return an em dash for missing values rather than empty string: a blank
 * table cell reads as a rendering bug, a dash reads as "there is nothing
 * here", which is the truth being communicated.
 */

const BYTE_UNITS = ['KB', 'MB', 'GB', 'TB', 'PB'] as const;

/**
 * Human-readable byte counts, 1024-based to match what Immich itself reports.
 *
 * Precision scales down as the number grows — "22.4 GB" is useful, "22.4183
 * GB" is noise — and whole bytes are never shown with decimals.
 */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) return '—';
  if (bytes < 1024) return `${bytes} B`;

  let value = bytes;
  let unitIndex = -1;
  do {
    value /= 1024;
    unitIndex += 1;
  } while (value >= 1024 && unitIndex < BYTE_UNITS.length - 1);

  const digits = value >= 100 ? 0 : value >= 10 ? 1 : 2;
  return `${value.toFixed(digits)} ${BYTE_UNITS[unitIndex] ?? 'PB'}`;
}

/**
 * A timestamp for humans, in their locale, without seconds.
 *
 * Admin surfaces show absolute times rather than "3 hours ago": relative time
 * is friendlier in a feed, but "when did the last library scan run" is a
 * question answered by a clock reading, not a feeling.
 */
export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}
