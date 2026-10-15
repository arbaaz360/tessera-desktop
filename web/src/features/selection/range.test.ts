import { describe, expect, it } from 'vitest';
import { rangeBetween } from './range';
import type { TimelineAsset } from '@/shared/api/timeline';

/**
 * The range function is where every shift-click edge case lives, so the edge
 * cases are the tests: forward, backward, single-item, and the three ways an
 * anchor can be unusable.
 */

const asset = (id: string): TimelineAsset => ({
  id,
  ratio: 1,
  thumbhash: null,
  isImage: true,
  isFavorite: false,
  durationMs: null,
  fileCreatedAt: '2024-01-01T00:00:00Z',
  livePhotoVideoId: null,
  city: null,
  country: null,
});

const assets = ['a', 'b', 'c', 'd', 'e'].map(asset);

describe('rangeBetween', () => {
  it('selects forward from the anchor', () => {
    expect(rangeBetween('b', 'd', assets)).toEqual(['b', 'c', 'd']);
  });

  it('selects backward when the target precedes the anchor', () => {
    expect(rangeBetween('d', 'a', assets)).toEqual(['a', 'b', 'c', 'd']);
  });

  it('collapses to a single id when anchor and target coincide', () => {
    expect(rangeBetween('c', 'c', assets)).toEqual(['c']);
  });

  it('spans the whole bucket between its endpoints', () => {
    expect(rangeBetween('a', 'e', assets)).toEqual(['a', 'b', 'c', 'd', 'e']);
  });

  it('returns null without an anchor', () => {
    expect(rangeBetween(null, 'c', assets)).toBeNull();
  });

  it('returns null when the anchor lives in another bucket', () => {
    // Cross-bucket ranges are not supported: the anchor id simply is not in
    // this bucket's array, and the caller falls back to a plain toggle.
    expect(rangeBetween('not-here', 'c', assets)).toBeNull();
  });

  it('returns null when the target is unknown', () => {
    expect(rangeBetween('a', 'not-here', assets)).toBeNull();
  });
});
