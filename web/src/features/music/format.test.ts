import { describe, expect, it } from 'vitest';
import {
  clampPercent,
  fingerprintHeadline,
  formatClockMs,
  formatWhen,
  recognitionHeadline,
} from './format';

describe('clampPercent', () => {
  it('passes sane values through', () => {
    expect(clampPercent(0)).toBe(0);
    expect(clampPercent(42.5)).toBe(42.5);
    expect(clampPercent(100)).toBe(100);
  });

  it('clamps float-division overshoot from the server', () => {
    // progress_percent is a Python float; 100.04 has been observed in the wild.
    expect(clampPercent(100.04)).toBe(100);
    expect(clampPercent(-3)).toBe(0);
  });

  it('treats absent or broken values as zero progress', () => {
    expect(clampPercent(null)).toBe(0);
    expect(clampPercent(undefined)).toBe(0);
    expect(clampPercent(Number.NaN)).toBe(0);
    // Infinity is garbage, not "complete" — showing a full bar for it would lie.
    expect(clampPercent(Number.POSITIVE_INFINITY)).toBe(0);
  });
});

describe('formatClockMs', () => {
  it('formats sub-hour durations as m:ss', () => {
    // 00:02:03.811 — the actual wire shape of a verified sandbox response,
    // after toDurationMs has normalised it to milliseconds.
    expect(formatClockMs(123_811)).toBe('2:04');
    expect(formatClockMs(5_000)).toBe('0:05');
    expect(formatClockMs(65_000)).toBe('1:05');
  });

  it('grows to h:mm:ss past the hour', () => {
    expect(formatClockMs(3_600_000)).toBe('1:00:00');
    expect(formatClockMs(7_384_005)).toBe('2:03:04');
  });

  it('rounds to the nearest second', () => {
    expect(formatClockMs(1_499)).toBe('0:01');
    expect(formatClockMs(1_500)).toBe('0:02');
  });

  it('returns null for unknowable durations rather than inventing 0:00', () => {
    expect(formatClockMs(null)).toBeNull();
    expect(formatClockMs(undefined)).toBeNull();
    expect(formatClockMs(-1)).toBeNull();
    expect(formatClockMs(Number.NaN)).toBeNull();
  });
});

describe('recognitionHeadline', () => {
  const base = { paused: false, identifierAvailable: true, processing: 0, pending: 0 };

  it('reports pause above everything else', () => {
    expect(
      recognitionHeadline({ ...base, paused: true, processing: 5, identifierAvailable: false }),
    ).toBe('Paused');
  });

  it('explains a stalled queue by its cause: dead identifier routes', () => {
    expect(recognitionHeadline({ ...base, identifierAvailable: false, pending: 10 })).toBe(
      'Recognition routes offline — queue preserved',
    );
  });

  it('shows active work, then backlog, then idle', () => {
    expect(recognitionHeadline({ ...base, processing: 3, pending: 9 })).toBe('Running — 3 active');
    expect(recognitionHeadline({ ...base, pending: 1200 })).toBe('Queued — 1,200 waiting');
    expect(recognitionHeadline(base)).toBe('Idle — nothing left to scan');
  });
});

describe('fingerprintHeadline', () => {
  const base = { paused: false, serviceAvailable: true, processing: 0, pending: 0 };

  it('mirrors the same precedence for the fingerprint index', () => {
    expect(fingerprintHeadline({ ...base, paused: true })).toBe('Indexing paused');
    expect(fingerprintHeadline({ ...base, serviceAvailable: false })).toBe(
      'Fingerprint service offline — queue preserved',
    );
    expect(fingerprintHeadline({ ...base, processing: 2 })).toBe('Indexing — 2 active');
    expect(fingerprintHeadline({ ...base, pending: 7 })).toBe('Queued — 7 waiting');
    expect(fingerprintHeadline(base)).toBe('Index up to date');
  });
});

describe('formatWhen', () => {
  it('says "never" for null — the sidecar sends null before a first run', () => {
    expect(formatWhen(null)).toBe('never');
    expect(formatWhen(undefined)).toBe('never');
    expect(formatWhen('')).toBe('never');
  });

  it('says "never" for garbage instead of "Invalid Date"', () => {
    expect(formatWhen('not-a-date')).toBe('never');
  });

  it('renders a real timestamp as a locale string', () => {
    // The exact text depends on the machine's locale, so assert shape, not value.
    expect(formatWhen('2026-08-08T05:13:17.187634Z')).not.toBe('never');
  });
});
