import { useEffect, useRef, useState } from 'react';
import { getCurrentWebview } from '@tauri-apps/api/webview';
import { isTauri } from '@/shared/api/core';
import { useImports } from './ImportContext';
import {
  LIBRARY,
  dropLabel,
  dropTargetAt,
  importTargetOf,
  isMacPlatform,
  targetFromElement,
  toCssPixels,
  urlsFromUriList,
  type DropTarget,
} from './dropTarget';

/**
 * Drag and drop from outside the app, anywhere in the window.
 *
 * TWO SOURCES OF THE SAME GESTURE:
 *
 *   - In Tauri the shell's drag-drop handler (Tauri 2's, on by default)
 *     catches files dragged from File Explorer or Finder and hands over
 *     their PATHS, with the pointer position. Paths are what the core wants:
 *     a file already in a library folder is indexed where it is, and only a
 *     file from outside is copied.
 *   - In a browser (and wherever a webview passes the drag through) the
 *     page's own drop event carries FILES (bytes) or a LINK. Bytes go up to
 *     the core one file at a time; a link is fetched once by the core.
 *
 * Either way the target is read from under the pointer (`dropTarget.ts`):
 * an album card or the album page adds to that album, anywhere else to the
 * library. The target is outlined while the drag hovers it, and the overlay
 * says in words what the drop will do.
 */
export function DropZone() {
  const { importPaths, importFiles, importUrls, notify } = useImports();
  const [drag, setDrag] = useState<{ count: number | null; target: DropTarget } | null>(null);

  // Outline the album the drag is over. The element is the page's own, so
  // this is an attribute toggle, not a React re-render of the whole list.
  const outlined = drag?.target.kind === 'album' ? drag.target.element : null;
  useEffect(() => {
    if (!outlined) return;
    outlined.setAttribute('data-drop-hover', '');
    return () => outlined.removeAttribute('data-drop-hover');
  }, [outlined]);

  // ------------------------------------------------------------ Tauri: paths
  useEffect(() => {
    if (!isTauri()) return;
    const mac = isMacPlatform();
    const at = (position: { x: number; y: number }) => {
      const css = toCssPixels(position, window.devicePixelRatio, mac);
      return dropTargetAt(css.x, css.y);
    };

    let cancelled = false;
    let unlisten: (() => void) | null = null;
    void getCurrentWebview()
      .onDragDropEvent((event) => {
        const payload = event.payload;
        if (payload.type === 'enter') {
          setDrag({ count: payload.paths.length, target: at(payload.position) });
        } else if (payload.type === 'over') {
          const target = at(payload.position);
          setDrag((current) =>
            current && sameTarget(current.target, target) ? current : { count: current?.count ?? null, target },
          );
        } else if (payload.type === 'leave') {
          setDrag(null);
        } else if (payload.type === 'drop') {
          const target = at(payload.position);
          setDrag(null);
          if (payload.paths.length > 0) void importPaths(payload.paths, importTargetOf(target));
          else notify('Nothing to add: the drop carried no file. Save the picture to a folder first, then drop the file.');
        }
      })
      .then((stop) => {
        if (cancelled) stop();
        else unlisten = stop;
      })
      .catch(() => undefined);

    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, [importPaths, notify]);

  // ------------------------------------------------------------ the page's own drag: bytes or links
  const depth = useRef(0);
  const internal = useRef(false);
  useEffect(() => {
    const fromOutside = (event: DragEvent) =>
      !internal.current &&
      !!event.dataTransfer &&
      [...event.dataTransfer.types].some((type) => type === 'Files' || type === 'text/uri-list');

    // A drag that starts on the page (a thumbnail dragged a few pixels) is
    // not an import; only drags from outside count.
    const onDragStart = () => {
      internal.current = true;
    };
    const onDragEnd = () => {
      internal.current = false;
    };
    const onDragEnter = (event: DragEvent) => {
      if (!fromOutside(event)) return;
      event.preventDefault();
      depth.current += 1;
      const count = event.dataTransfer?.items?.length ?? null;
      setDrag({ count: count || null, target: targetFromElement(event.target as Element | null) });
    };
    const onDragOver = (event: DragEvent) => {
      if (!fromOutside(event)) return;
      event.preventDefault();
      if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy';
      const target = targetFromElement(event.target as Element | null);
      setDrag((current) =>
        current && sameTarget(current.target, target) ? current : { count: current?.count ?? null, target },
      );
    };
    const onDragLeave = (event: DragEvent) => {
      if (!fromOutside(event)) return;
      depth.current = Math.max(0, depth.current - 1);
      if (depth.current === 0) setDrag(null);
    };
    const onDrop = (event: DragEvent) => {
      if (!fromOutside(event)) return;
      event.preventDefault();
      depth.current = 0;
      setDrag(null);
      const target = importTargetOf(targetFromElement(event.target as Element | null));
      const files = [...(event.dataTransfer?.files ?? [])];
      if (files.length > 0) {
        void importFiles(files, target);
        return;
      }
      const urls = urlsFromUriList(event.dataTransfer?.getData('text/uri-list') ?? '');
      if (urls.length > 0) void importUrls(urls, target);
      else notify('Nothing to add: the drop carried no file or link.');
    };

    document.addEventListener('dragstart', onDragStart);
    document.addEventListener('dragend', onDragEnd);
    window.addEventListener('dragenter', onDragEnter);
    window.addEventListener('dragover', onDragOver);
    window.addEventListener('dragleave', onDragLeave);
    window.addEventListener('drop', onDrop);
    return () => {
      document.removeEventListener('dragstart', onDragStart);
      document.removeEventListener('dragend', onDragEnd);
      window.removeEventListener('dragenter', onDragEnter);
      window.removeEventListener('dragover', onDragOver);
      window.removeEventListener('dragleave', onDragLeave);
      window.removeEventListener('drop', onDrop);
    };
  }, [importFiles, importUrls, notify]);

  if (!drag) return null;

  return (
    <div
      data-testid="drop-overlay"
      className="pointer-events-none fixed inset-0 z-[60] flex items-end justify-center bg-accent/10 p-6"
      style={{ boxShadow: 'inset 0 0 0 3px var(--color-accent)' }}
    >
      <p
        role="status"
        className="mb-6 rounded-lg border border-accent bg-overlay px-4 py-2.5 text-sm font-medium text-ink shadow-2xl"
      >
        {dropLabel(drag.target ?? LIBRARY, drag.count)}
      </p>
    </div>
  );
}

function sameTarget(a: DropTarget, b: DropTarget): boolean {
  if (a.kind !== b.kind) return false;
  return a.kind === 'library' || (b.kind === 'album' && a.albumId === b.albumId && a.element === b.element);
}
