import { describe, expect, it } from 'vitest';
import { formatDimensions } from './info';

describe('formatDimensions', () => {
  it('shows both edges and the megapixel count', () => {
    expect(formatDimensions(6000, 4000)).toBe('6,000 × 4,000 (24.0 MP)');
    expect(formatDimensions(1080, 1350)).toBe('1,080 × 1,350 (1.5 MP)');
  });

  it('returns null when either edge is missing or zero', () => {
    expect(formatDimensions(null, 4000)).toBeNull();
    expect(formatDimensions(6000, undefined)).toBeNull();
    expect(formatDimensions(0, 4000)).toBeNull();
  });
});
