/**
 * Copying an image to the system clipboard.
 *
 * Two browser constraints shape all of this, and neither is obvious:
 *
 * 1. SECURE CONTEXT. `navigator.clipboard` exists only on HTTPS or on
 *    localhost. Reaching this app as `http://samurai.local:2283` - a perfectly
 *    ordinary way to open it on the LAN - leaves the API undefined. The old
 *    Album Sentinel browser extension existed partly to work around exactly
 *    this. So the UI must ASK whether copying is possible before offering it,
 *    and say something useful when it is not.
 *
 * 2. PNG ONLY, in practice. Chrome's async clipboard accepts `image/png` and
 *    little else; handing it a JPEG blob straight from the server throws
 *    "NotAllowedError: Type image/jpeg not supported". Everything therefore
 *    goes through a canvas re-encode to PNG. That is lossless for what lands
 *    on the clipboard, and it is what the browser's own "Copy image" does.
 *
 * Safari additionally requires the ClipboardItem to be constructed with a
 * PROMISE of the blob rather than an awaited blob, or it rejects the write as
 * happening outside a user gesture. Passing the promise satisfies both engines,
 * so that is what this does even though Chrome does not need it.
 */

/** Whether the clipboard image API is usable in this context at all. */
export function canCopyImages(): boolean {
  return (
    typeof window !== 'undefined' &&
    window.isSecureContext &&
    typeof navigator.clipboard?.write === 'function' &&
    typeof window.ClipboardItem === 'function'
  );
}

/** Why copying is unavailable, phrased for someone who can act on it. */
export const INSECURE_CONTEXT_HINT =
  'Copying needs a secure connection. Open this page as https://immich.samurai.local (or via localhost) and the copy button appears.';

async function toPngBlob(source: Blob): Promise<Blob> {
  const bitmap = await createImageBitmap(source);
  try {
    const canvas = document.createElement('canvas');
    canvas.width = bitmap.width;
    canvas.height = bitmap.height;
    const context = canvas.getContext('2d');
    if (!context) throw new Error('Could not get a 2D canvas context');
    context.drawImage(bitmap, 0, 0);

    return await new Promise<Blob>((resolve, reject) => {
      canvas.toBlob(
        (blob) => (blob ? resolve(blob) : reject(new Error('Canvas produced no image data'))),
        'image/png',
      );
    });
  } finally {
    // Bitmaps hold decoded pixels — several hundred MB for a large photo — and
    // are not garbage collected promptly. Release explicitly.
    bitmap.close();
  }
}

/**
 * Fetch an image URL and put it on the clipboard as a PNG.
 *
 * Throws with a message worth showing: the caller surfaces it directly rather
 * than translating error codes.
 */
export async function copyImageToClipboard(url: string): Promise<void> {
  if (!canCopyImages()) throw new Error(INSECURE_CONTEXT_HINT);

  // `credentials: 'include'` because asset URLs authenticate by session cookie
  // (and carry ?sessionKey= when an account switch is active).
  const response = await fetch(url, { credentials: 'include' });
  if (!response.ok) throw new Error(`Could not load the image (${response.status})`);

  const source = await response.blob();
  const png = toPngBlob(source);

  await navigator.clipboard.write([new ClipboardItem({ 'image/png': png })]);
}
