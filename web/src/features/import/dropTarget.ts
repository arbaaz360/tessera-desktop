import type { ImportTarget } from './api';

/**
 * Where a drop lands, read from the page itself.
 *
 * Anything that accepts pictures for an album says so in the DOM:
 * `data-drop-album-id` and `data-drop-album-name` on an album card, a row in
 * the album list, or the album page as a whole. Everything else - the grid,
 * the sidebar, the welcome page - is the library. One attribute, so a new
 * drop target is one line of markup and no registry.
 */

export type DropTarget =
  | { kind: 'album'; albumId: string; albumName: string; element: Element }
  | { kind: 'library' };

export const LIBRARY: DropTarget = { kind: 'library' };

/** The target an element is inside, nearest first. */
export function targetFromElement(element: Element | null): DropTarget {
  const holder = element?.closest('[data-drop-album-id]');
  const albumId = holder?.getAttribute('data-drop-album-id');
  if (!holder || !albumId) return LIBRARY;
  return { kind: 'album', albumId, albumName: holder.getAttribute('data-drop-album-name') ?? 'the album', element: holder };
}

/** The target under a point in CSS pixels. */
export function dropTargetAt(x: number, y: number, doc: Document = document): DropTarget {
  const element = typeof doc.elementFromPoint === 'function' ? doc.elementFromPoint(x, y) : null;
  return targetFromElement(element);
}

/**
 * A position from Tauri's drag-drop events, in CSS pixels.
 *
 * Tauri labels it physical, and on Windows it is: client pixels, so a 150 %
 * display needs dividing by 1.5. On macOS the same field carries the view's
 * own points, which are CSS pixels already; dividing by a Retina screen's 2
 * would aim at the wrong half of the window.
 */
export function toCssPixels(position: { x: number; y: number }, devicePixelRatio: number, isMac: boolean) {
  const scale = isMac || !devicePixelRatio ? 1 : devicePixelRatio;
  return { x: position.x / scale, y: position.y / scale };
}

export function isMacPlatform(): boolean {
  return typeof navigator !== 'undefined' && /Mac/i.test(navigator.platform || navigator.userAgent);
}

export function importTargetOf(target: DropTarget): ImportTarget | null {
  return target.kind === 'album' ? { albumId: target.albumId, albumName: target.albumName } : null;
}

/** What the overlay says. */
export function dropLabel(target: DropTarget, count: number | null): string {
  const what = count === null ? 'pictures' : count === 1 ? '1 item' : `${count.toLocaleString()} items`;
  return target.kind === 'album' ? `Drop to add ${what} to “${target.albumName}”` : `Drop to add ${what} to your library`;
}

/** Links in a browser drag (`text/uri-list`), comments and blanks dropped. */
export function urlsFromUriList(text: string): string[] {
  return text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0 && !line.startsWith('#') && /^https?:\/\//i.test(line));
}
