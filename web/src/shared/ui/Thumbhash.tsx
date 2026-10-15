import { useEffect, useMemo, useRef, useState } from 'react';
import { thumbHashToRGBA } from 'thumbhash';

/**
 * Blurred placeholder decoded from a thumbhash.
 *
 * A thumbhash is ~28 bytes describing an image's colours and rough structure.
 * The server already sends one per asset in the bucket payload, so we can paint
 * a recognisable blur for every tile the instant the grid lays out — before a
 * single JPEG has been requested.
 *
 * This is most of why the grid feels fast. Without it, scrolling shows grey
 * rectangles and the eye reads "broken"; with it, the page looks complete and
 * then sharpens. Same network time, entirely different experience.
 *
 * Decoding happens on a canvas and is cached per hash — the same asset scrolled
 * past twice does not decode twice.
 */

const cache = new Map<string, string>();
const MAX_CACHE = 600;

function decodeToDataUrl(hash: string): string | null {
  const cached = cache.get(hash);
  if (cached) return cached;

  try {
    const bytes = Uint8Array.from(atob(hash), (c) => c.charCodeAt(0));
    const { w, h, rgba } = thumbHashToRGBA(bytes);

    const canvas = document.createElement('canvas');
    canvas.width = w;
    canvas.height = h;

    const context = canvas.getContext('2d');
    if (!context) return null;

    const imageData = context.createImageData(w, h);
    imageData.data.set(rgba);
    context.putImageData(imageData, 0, 0);

    const url = canvas.toDataURL();

    // Bound the cache. Naive eviction is fine here: entries are tiny and the
    // access pattern is a scrolling window, so the oldest really is the least
    // likely to be needed next.
    if (cache.size >= MAX_CACHE) {
      const oldest = cache.keys().next().value;
      if (oldest) cache.delete(oldest);
    }
    cache.set(hash, url);

    return url;
  } catch {
    // A malformed hash should degrade to a plain surface, never crash a grid.
    return null;
  }
}

export function Thumbhash({ hash, className }: { hash: string | null; className?: string }) {
  const url = useMemo(() => (hash ? decodeToDataUrl(hash) : null), [hash]);

  if (!url) {
    return <div className={className} style={{ background: 'var(--color-raised)' }} aria-hidden />;
  }

  return (
    <img
      src={url}
      alt=""
      aria-hidden
      className={className}
      style={{ objectFit: 'cover', width: '100%', height: '100%' }}
    />
  );
}

/**
 * An image that fades in over its own thumbhash once decoded.
 *
 * `loading="lazy"` and `decoding="async"` matter more than they look: together
 * they keep image decoding off the main thread, so fast scrolling stays smooth
 * instead of janking on every tile that enters the viewport.
 */
export function ProgressiveImage({
  src,
  alt,
  hash,
  className,
}: {
  src: string;
  alt: string;
  hash: string | null;
  className?: string;
}) {
  const [loaded, setLoaded] = useState(false);
  const imageRef = useRef<HTMLImageElement>(null);

  // An image already in the browser cache can fire `load` before React attaches
  // the handler, leaving the placeholder up forever. Checking `complete` on
  // mount closes that race.
  useEffect(() => {
    if (imageRef.current?.complete) setLoaded(true);
  }, [src]);

  return (
    <div className={`relative overflow-hidden ${className ?? ''}`}>
      {!loaded && <div className="absolute inset-0"><Thumbhash hash={hash} className="h-full w-full" /></div>}
      <img
        ref={imageRef}
        src={src}
        alt={alt}
        loading="lazy"
        decoding="async"
        onLoad={() => setLoaded(true)}
        className="h-full w-full object-cover transition-opacity duration-300"
        style={{ opacity: loaded ? 1 : 0, transitionTimingFunction: 'var(--ease-out-soft)' }}
      />
    </div>
  );
}
