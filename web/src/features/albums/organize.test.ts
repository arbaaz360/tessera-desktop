import { describe, expect, it } from 'vitest';
import {
  albumDateMs,
  albumYearLabel,
  buildRenderList,
  groupAlbumsByYear,
  sortAlbums,
  UNDATED_LABEL,
  type AlbumLike,
} from './organize';

/**
 * Fixture builder. Only the fields `organize.ts` reads — that the functions
 * accept this subset instead of the full `AlbumResponseDto` is itself part of
 * the contract under test.
 */
function album(overrides: Partial<AlbumLike> & { id: string }): AlbumLike {
  return {
    albumName: overrides.id,
    assetCount: 0,
    createdAt: '2024-01-15T00:00:00.000Z',
    ...overrides,
  };
}

describe('albumDateMs', () => {
  it('prefers endDate over createdAt', () => {
    const a = album({ id: 'a', createdAt: '2024-01-01T00:00:00Z', endDate: '2021-06-15T00:00:00Z' });
    expect(albumDateMs(a)).toBe(Date.parse('2021-06-15T00:00:00Z'));
  });

  it('falls back to createdAt when endDate is missing', () => {
    const a = album({ id: 'a', createdAt: '2024-01-01T00:00:00Z' });
    expect(albumDateMs(a)).toBe(Date.parse('2024-01-01T00:00:00Z'));
  });

  it('returns null when nothing parses', () => {
    const a = album({ id: 'a', createdAt: 'not a date', endDate: 'also not' });
    expect(albumDateMs(a)).toBeNull();
  });
});

describe('albumYearLabel', () => {
  it('labels by the effective date year', () => {
    expect(albumYearLabel(album({ id: 'a', endDate: '2019-12-31T12:00:00Z' }))).toBe('2019');
  });

  it('labels undated albums as Undated', () => {
    expect(albumYearLabel(album({ id: 'a', createdAt: '' }))).toBe(UNDATED_LABEL);
  });
});

describe('sortAlbums', () => {
  const fixtures = [
    album({ id: 'b', albumName: 'Beach', assetCount: 5, endDate: '2022-07-01T00:00:00Z' }),
    album({ id: 'a', albumName: 'Alps', assetCount: 50, endDate: '2020-01-01T00:00:00Z' }),
    album({ id: 'u', albumName: 'Unsorted', assetCount: 5, createdAt: 'garbage' }),
    album({ id: 'c', albumName: 'City', assetCount: 20, endDate: '2024-03-01T00:00:00Z' }),
  ];

  it('sorts by name A–Z', () => {
    expect(sortAlbums(fixtures, 'name').map((a) => a.id)).toEqual(['a', 'b', 'c', 'u']);
  });

  it('sorts names with embedded numbers numerically', () => {
    const numbered = [
      album({ id: 'ten', albumName: 'Trip 10' }),
      album({ id: 'two', albumName: 'Trip 2' }),
    ];
    expect(sortAlbums(numbered, 'name').map((a) => a.id)).toEqual(['two', 'ten']);
  });

  it('sorts newest first with undated albums last', () => {
    expect(sortAlbums(fixtures, 'newest').map((a) => a.id)).toEqual(['c', 'b', 'a', 'u']);
  });

  it('sorts oldest first with undated albums still last', () => {
    expect(sortAlbums(fixtures, 'oldest').map((a) => a.id)).toEqual(['a', 'b', 'c', 'u']);
  });

  it('sorts by most assets, breaking count ties by name', () => {
    // Beach and Unsorted both hold 5 — Beach wins alphabetically.
    expect(sortAlbums(fixtures, 'most').map((a) => a.id)).toEqual(['a', 'c', 'b', 'u']);
  });

  it('does not mutate its input', () => {
    const before = fixtures.map((a) => a.id);
    sortAlbums(fixtures, 'name');
    expect(fixtures.map((a) => a.id)).toEqual(before);
  });
});

describe('groupAlbumsByYear', () => {
  const fixtures = [
    album({ id: 'a1', endDate: '2024-05-01T00:00:00Z' }),
    album({ id: 'a2', endDate: '2024-01-01T00:00:00Z' }),
    album({ id: 'b1', endDate: '2021-08-01T00:00:00Z' }),
    album({ id: 'u1', createdAt: 'garbage' }),
  ];

  it('orders years descending with Undated last', () => {
    const groups = groupAlbumsByYear(fixtures, 'desc');
    expect(groups.map((g) => g.label)).toEqual(['2024', '2021', UNDATED_LABEL]);
  });

  it('orders years ascending with Undated still last', () => {
    const groups = groupAlbumsByYear(fixtures, 'asc');
    expect(groups.map((g) => g.label)).toEqual(['2021', '2024', UNDATED_LABEL]);
  });

  it('preserves the incoming album order inside each group', () => {
    const groups = groupAlbumsByYear(fixtures, 'desc');
    expect(groups[0]?.albums.map((a) => a.id)).toEqual(['a1', 'a2']);
  });
});

describe('buildRenderList', () => {
  const fixtures = [
    album({ id: 'a1', endDate: '2024-05-01T00:00:00Z' }),
    album({ id: 'a2', endDate: '2024-01-01T00:00:00Z' }),
    album({ id: 'a3', endDate: '2024-01-01T00:00:00Z' }),
    album({ id: 'b1', endDate: '2021-08-01T00:00:00Z' }),
  ];

  it('chunks a flat grid into rows of `columns`', () => {
    const items = buildRenderList(fixtures, {
      view: 'grid',
      columns: 3,
      groupOrder: null,
      collapsed: new Set(),
    });
    expect(items.map((i) => i.kind)).toEqual(['grid-row', 'grid-row']);
    expect(items[0]?.kind === 'grid-row' && items[0].albums.length).toBe(3);
    expect(items[1]?.kind === 'grid-row' && items[1].albums.length).toBe(1);
  });

  it('emits one list row per album in list view', () => {
    const items = buildRenderList(fixtures, {
      view: 'list',
      columns: 3,
      groupOrder: null,
      collapsed: new Set(),
    });
    expect(items.map((i) => i.kind)).toEqual(['list-row', 'list-row', 'list-row', 'list-row']);
  });

  it('interleaves year headers when grouped, restarting rows per group', () => {
    const items = buildRenderList(fixtures, {
      view: 'grid',
      columns: 2,
      groupOrder: 'desc',
      collapsed: new Set(),
    });
    // 2024 holds three albums → two rows; 2021 holds one → one row. Rows never
    // span a group boundary.
    expect(items.map((i) => i.kind)).toEqual([
      'header',
      'grid-row',
      'grid-row',
      'header',
      'grid-row',
    ]);
  });

  it('keeps the header but drops the rows of a collapsed group', () => {
    const items = buildRenderList(fixtures, {
      view: 'grid',
      columns: 2,
      groupOrder: 'desc',
      collapsed: new Set(['2024']),
    });
    expect(items.map((i) => i.kind)).toEqual(['header', 'header', 'grid-row']);
    expect(items[0]?.kind === 'header' && items[0].collapsed).toBe(true);
    expect(items[0]?.kind === 'header' && items[0].count).toBe(3);
  });

  it('treats columns below one as one instead of looping forever', () => {
    const items = buildRenderList(fixtures, {
      view: 'grid',
      columns: 0,
      groupOrder: null,
      collapsed: new Set(),
    });
    expect(items).toHaveLength(4);
  });
});
