import { describe, expect, it } from 'vitest';
import { dropLabel, targetFromElement, toCssPixels, urlsFromUriList } from './dropTarget';
import { addCounts, NO_COUNTS, summarizeCounts } from './api';

describe('drop targets', () => {
  it('finds the nearest album around the element under the pointer, else the library', () => {
    document.body.innerHTML = `
      <main data-drop-album-id="5" data-drop-album-name="A">
        <div data-drop-album-id="7" data-drop-album-name="Lakes"><img id="cover"></div>
        <p id="title">A</p>
      </main>
      <aside id="sidebar"></aside>`;

    const cover = targetFromElement(document.getElementById('cover'));
    expect(cover).toMatchObject({ kind: 'album', albumId: '7', albumName: 'Lakes' });
    expect(targetFromElement(document.getElementById('title'))).toMatchObject({ kind: 'album', albumId: '5' });
    expect(targetFromElement(document.getElementById('sidebar'))).toEqual({ kind: 'library' });
    expect(targetFromElement(null)).toEqual({ kind: 'library' });
  });

  it('scales Windows positions by the display scale and leaves macOS points alone', () => {
    expect(toCssPixels({ x: 300, y: 150 }, 1.5, false)).toEqual({ x: 200, y: 100 });
    expect(toCssPixels({ x: 300, y: 150 }, 2, true)).toEqual({ x: 300, y: 150 });
    expect(toCssPixels({ x: 300, y: 150 }, 0, false)).toEqual({ x: 300, y: 150 });
  });

  it('says what the drop will do, and where', () => {
    const album = { kind: 'album' as const, albumId: '5', albumName: 'A', element: document.body };
    expect(dropLabel(album, 3)).toBe('Drop to add 3 items to “A”');
    expect(dropLabel({ kind: 'library' }, 1)).toBe('Drop to add 1 item to your library');
    expect(dropLabel({ kind: 'library' }, null)).toBe('Drop to add pictures to your library');
  });

  it('reads http links from a uri-list and nothing else', () => {
    expect(urlsFromUriList('# from a browser\r\nhttps://example.test/a.jpg\r\nfile:///C:/x.jpg\r\n\r\nhttp://example.test/b')).toEqual([
      'https://example.test/a.jpg',
      'http://example.test/b',
    ]);
  });
});

describe('import counts', () => {
  it('summarises without the zeros, in the order a person reads them', () => {
    expect(summarizeCounts({ added: 2, already: 1, copied: 3, skipped: 1, folders: 1 })).toBe(
      '2 added · 3 copied in · 1 already there · 1 folder · 1 skipped',
    );
    expect(summarizeCounts(NO_COUNTS)).toBe('nothing yet');
    expect(addCounts({ ...NO_COUNTS, copied: 1 }, { ...NO_COUNTS, copied: 2, skipped: 1 })).toEqual({
      ...NO_COUNTS,
      copied: 3,
      skipped: 1,
    });
  });
});
