import { describe, expect, it } from 'vitest';
import { formatDimensions, formatExposure, formatLens } from './info';

describe('formatExposure', () => {
  it('leaves a fraction alone', () => {
    // Immich usually stores it this way already; reformatting would only risk
    // rounding a value that was already exact.
    expect(formatExposure('1/125')).toBe('1/125');
  });

  it('turns a sub-second decimal into the fraction photographers read', () => {
    expect(formatExposure('0.008')).toBe('1/125');
    expect(formatExposure('0.5')).toBe('1/2');
  });

  it('keeps long exposures in seconds, with a unit', () => {
    // "2" alone would read as a 1/2-second fraction to anyone skimming.
    expect(formatExposure('2')).toBe('2s');
    expect(formatExposure('1')).toBe('1s');
  });

  it('returns null for a missing value rather than an empty string', () => {
    expect(formatExposure(null)).toBeNull();
    expect(formatExposure(undefined)).toBeNull();
    expect(formatExposure('')).toBeNull();
  });

  it('passes through anything it cannot parse', () => {
    // Better to show the raw EXIF string than to invent a number from it.
    expect(formatExposure('auto')).toBe('auto');
    expect(formatExposure('0')).toBe('0');
  });
});

describe('formatLens', () => {
  it('joins the settings that are present', () => {
    expect(formatLens({ focalLength: 24, fNumber: 1.8, iso: 400, exposureTime: '1/125' })).toBe(
      '24 mm · f/1.8 · 1/125 · ISO 400',
    );
  });

  it('drops what is missing instead of padding with dashes', () => {
    expect(formatLens({ focalLength: 50, iso: 100 })).toBe('50 mm · ISO 100');
  });

  it('rounds focal length — nobody needs 23.999 mm', () => {
    expect(formatLens({ focalLength: 23.999 })).toBe('24 mm');
  });

  it('returns null when there is nothing to say, so the section can hide', () => {
    expect(formatLens({})).toBeNull();
    expect(formatLens({ focalLength: null, fNumber: null, iso: null, exposureTime: null })).toBeNull();
  });

  it('treats a zero as absent, because a 0mm lens is missing data', () => {
    expect(formatLens({ focalLength: 0, iso: 0 })).toBeNull();
  });
});

describe('formatDimensions', () => {
  it('reports pixels and the megapixel count people compare', () => {
    expect(formatDimensions(6000, 4000)).toBe('6,000 × 4,000 (24.0 MP)');
  });

  it('returns null when either side is missing', () => {
    expect(formatDimensions(6000, null)).toBeNull();
    expect(formatDimensions(null, 4000)).toBeNull();
    expect(formatDimensions(undefined, undefined)).toBeNull();
  });
});
