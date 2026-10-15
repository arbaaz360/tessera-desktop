import { describe, expect, it } from 'vitest';
import { formatBytes, formatDateTime } from './format';

describe('formatBytes', () => {
  it('shows whole bytes below 1 KB', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(1023)).toBe('1023 B');
  });

  it('scales through the units at base 1024', () => {
    expect(formatBytes(1024)).toBe('1.00 KB');
    expect(formatBytes(1024 * 1024)).toBe('1.00 MB');
    expect(formatBytes(5.5 * 1024 ** 3)).toBe('5.50 GB');
  });

  it('reduces precision as the number grows', () => {
    expect(formatBytes(22 * 1024 ** 3)).toBe('22.0 GB');
    expect(formatBytes(250 * 1024 ** 3)).toBe('250 GB');
  });

  it('renders a dash for missing or nonsense values', () => {
    expect(formatBytes(null)).toBe('—');
    expect(formatBytes(undefined)).toBe('—');
    expect(formatBytes(-1)).toBe('—');
    expect(formatBytes(Number.NaN)).toBe('—');
  });
});

describe('formatDateTime', () => {
  it('renders a dash for null and invalid input', () => {
    expect(formatDateTime(null)).toBe('—');
    expect(formatDateTime(undefined)).toBe('—');
    expect(formatDateTime('not a date')).toBe('—');
  });

  it('renders a locale string for a valid ISO timestamp', () => {
    // The exact text is locale-dependent, so assert shape, not content: the
    // year must appear and the placeholder dash must not.
    const result = formatDateTime('2026-08-22T18:30:02.737Z');
    expect(result).toContain('2026');
    expect(result).not.toBe('—');
  });
});
