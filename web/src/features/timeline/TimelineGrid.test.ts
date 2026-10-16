import { describe, expect, it } from 'vitest';
import type { TimelineAsset } from '@/shared/api/timeline';
import { layoutAssets } from './layout';
import { bucketItemStyle, rowWidth } from './TimelineGrid';

/**
 * Two layout faults seen in the Windows UI test (2026-10-16), both invisible
 * to jsdom, so pinned here at the functions that decide them.
 */

function asset(id: string, ratio: number): TimelineAsset {
  return {
    id,
    ratio,
    thumbhash: null,
    isImage: true,
    isFavorite: false,
    durationMs: null,
    fileCreatedAt: '2024-04-01T12:52:00.000Z',
    livePhotoVideoId: null,
  };
}

describe('bucketItemStyle', () => {
  it('places a month with top, not a transform, so its sticky header sticks to the top of the view', () => {
    const style = bucketItemStyle(267);
    expect(style.top).toBe(267);
    expect(style.position).toBe('absolute');
    expect(style).not.toHaveProperty('transform');
  });
});

describe('rowWidth', () => {
  it('lays rows out inside the month padding, so no picture runs under the right edge', () => {
    // The main grid in a 1280-wide window: a 1040 px scroller.
    const width = rowWidth(1040);
    expect(width).toBe(1008);

    const assets = [asset('a', 1.6), asset('b', 1.6), asset('c', 1.6), asset('d', 1.6), asset('e', 1.33)];
    const { rows } = layoutAssets(assets, { containerWidth: width, targetRowHeight: 220, gap: 4 });
    const firstRow = rows[0]!;
    const last = firstRow.tiles.at(-1)!;
    // A full row ends exactly at the content edge: 16 px short of the scroller's.
    expect(last.x + last.width).toBeCloseTo(1008, 5);
  });

  it('never goes negative in a collapsed container', () => {
    expect(rowWidth(0)).toBe(0);
    expect(rowWidth(20)).toBe(0);
  });
});
