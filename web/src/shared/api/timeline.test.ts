import { describe, expect, it } from 'vitest';
import { toDurationMs } from './timeline';

/**
 * The two server versions disagree on the wire type of `duration`:
 * v2.7.5 sends "HH:MM:SS.mmm" strings, v3 sends integer milliseconds.
 * Both are reachable from this app, so both must normalise to a number.
 *
 * The string form is what actually shipped broken: Number.isFinite("00:00:02.066")
 * is false, so every video badge rendered empty against a v2.7.5 backend.
 */
describe('toDurationMs', () => {
  it('passes v3 integer milliseconds through', () => {
    expect(toDurationMs(2066)).toBe(2066);
    expect(toDurationMs(30_139)).toBe(30_139);
    expect(toDurationMs(0)).toBe(0);
  });

  it('parses the v2.7.5 "HH:MM:SS.mmm" string form', () => {
    expect(toDurationMs('00:00:02.066')).toBe(2066);
    expect(toDurationMs('00:00:30.139')).toBe(30_139);
    expect(toDurationMs('00:01:00.000')).toBe(60_000);
    expect(toDurationMs('01:00:00.000')).toBe(3_600_000);
    expect(toDurationMs('02:03:04.005')).toBe(7_384_005);
  });

  it('handles a missing fractional part and short fractions', () => {
    expect(toDurationMs('00:00:05')).toBe(5000);
    expect(toDurationMs('00:00:05.5')).toBe(5500);
    expect(toDurationMs('00:00:05.05')).toBe(5050);
  });

  it('returns null for absent or unparseable values', () => {
    expect(toDurationMs(null)).toBeNull();
    expect(toDurationMs(undefined)).toBeNull();
    expect(toDurationMs('')).toBeNull();
    expect(toDurationMs('not a duration')).toBeNull();
    expect(toDurationMs(Number.NaN)).toBeNull();
    expect(toDurationMs(Number.POSITIVE_INFINITY)).toBeNull();
  });
});
