import type { TimelineAsset } from '@/shared/api/timeline';

/**
 * Shift-click range resolution, as a pure function.
 *
 * Given the anchor (the last asset clicked), the target (the asset just
 * shift-clicked) and the bucket's assets in display order, return the ids of
 * every asset between them, inclusive, in either direction.
 *
 * Returns `null` when a range cannot be formed — the anchor is missing, or
 * either endpoint is not in this bucket's array. The caller treats `null` as
 * "fall back to a plain toggle", which is exactly what a user expects when a
 * shift-click has no meaningful anchor to stretch from.
 *
 * Kept out of the React context deliberately: index arithmetic with four edge
 * cases is the part worth unit-testing, and testing it should not require
 * rendering a provider.
 */
export function rangeBetween(
  anchorId: string | null,
  targetId: string,
  assets: readonly TimelineAsset[],
): string[] | null {
  if (!anchorId) return null;

  const from = assets.findIndex((asset) => asset.id === anchorId);
  const to = assets.findIndex((asset) => asset.id === targetId);
  if (from === -1 || to === -1) return null;

  const [lo, hi] = from <= to ? [from, to] : [to, from];
  const ids: string[] = [];
  for (let i = lo; i <= hi; i++) {
    ids.push(assets[i]!.id);
  }
  return ids;
}
