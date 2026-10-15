import { describe, expect, it } from 'vitest';
import {
  DEFAULT_PREFS,
  SUGGESTION_SIZE_MAX,
  SUGGESTION_SIZE_MIN,
  parseViewPrefs,
} from './prefs';

/**
 * Only the parser is tested — it is the part with decisions in it. The hook
 * around it is composition of `useState` and `useEffect`, which React already
 * tests better than we can.
 */
describe('parseViewPrefs', () => {
  it('returns defaults for absent or empty storage', () => {
    expect(parseViewPrefs(null)).toEqual(DEFAULT_PREFS);
    expect(parseViewPrefs('')).toEqual(DEFAULT_PREFS);
  });

  it('returns defaults for malformed JSON and non-objects', () => {
    expect(parseViewPrefs('{not json')).toEqual(DEFAULT_PREFS);
    expect(parseViewPrefs('"a string"')).toEqual(DEFAULT_PREFS);
    expect(parseViewPrefs('null')).toEqual(DEFAULT_PREFS);
  });

  it('round-trips a valid value', () => {
    const stored = JSON.stringify({ sort: 'name', view: 'list', groupByYear: true, suggestionSize: 160 });
    expect(parseViewPrefs(stored)).toEqual({
      sort: 'name',
      view: 'list',
      groupByYear: true,
      suggestionSize: 160,
    });
  });

  it('falls back per field, keeping the fields that are valid', () => {
    const stored = JSON.stringify({ sort: 'by-vibes', view: 'list', groupByYear: 'yes' });
    expect(parseViewPrefs(stored)).toEqual({
      sort: DEFAULT_PREFS.sort,
      view: 'list',
      groupByYear: DEFAULT_PREFS.groupByYear,
      suggestionSize: DEFAULT_PREFS.suggestionSize,
    });
  });

  it('clamps the suggestion size into its bounds', () => {
    const parse = (suggestionSize: unknown) =>
      parseViewPrefs(JSON.stringify({ suggestionSize })).suggestionSize;
    expect(parse(4)).toBe(SUGGESTION_SIZE_MIN);
    expect(parse(10_000)).toBe(SUGGESTION_SIZE_MAX);
    expect(parse(150.6)).toBe(151);
    expect(parse('big')).toBe(DEFAULT_PREFS.suggestionSize);
    expect(parse(Number.NaN)).toBe(DEFAULT_PREFS.suggestionSize);
  });
});
