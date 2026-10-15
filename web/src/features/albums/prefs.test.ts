import { describe, expect, it } from 'vitest';
import { DEFAULT_PREFS, parseViewPrefs } from './prefs';

/**
 * Only the parser is tested - it is the part with decisions in it. The hook
 * around it is composition of `useState` and `useEffect`.
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
    const stored = JSON.stringify({ sort: 'name', view: 'list', groupByYear: true });
    expect(parseViewPrefs(stored)).toEqual({ sort: 'name', view: 'list', groupByYear: true });
  });

  it('falls back per field, keeping the fields that are valid', () => {
    const stored = JSON.stringify({ sort: 'by-vibes', view: 'list', groupByYear: 'yes' });
    expect(parseViewPrefs(stored)).toEqual({
      sort: DEFAULT_PREFS.sort,
      view: 'list',
      groupByYear: DEFAULT_PREFS.groupByYear,
    });
  });

  it('ignores fields from older versions of the app', () => {
    const stored = JSON.stringify({ sort: 'most', view: 'grid', groupByYear: false, suggestionSize: 160 });
    expect(parseViewPrefs(stored)).toEqual({ sort: 'most', view: 'grid', groupByYear: false });
  });
});
