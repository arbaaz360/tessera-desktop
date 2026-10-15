import type { ReactNode } from 'react';
import { Link } from '@tanstack/react-router';
import type { AlbumResponseDto } from '@immich/sdk';
import { formatBytes, formatDateTime } from '@/shared/format';
import {
  formatDimensions,
  formatLens,
  useAlbumStats,
  useAssetAlbums,
  useAssetInfo,
} from './info';

/**
 * The viewer's info sidebar.
 *
 * Answers the questions you have while looking AT a photo rather than at the
 * grid: what shot this, how big is it, and — the one Immich's own UI never
 * answered — which album is it in and how large is that album.
 *
 * Sits BESIDE the image, not over it. An overlay panel would cover the thing
 * being described, which defeats the purpose; the image area simply gets
 * narrower while the panel is open.
 *
 * Everything degrades independently. EXIF, album membership and album size are
 * three separate requests against two different services, and any of them can
 * be missing, slow or dead — a photo imported without metadata, an asset in no
 * album at all, a stopped sidecar. Each section renders what it has and says
 * nothing about what it does not, so a dead sidecar costs you the size line
 * and nothing else.
 */
export function InfoPanel({
  assetId,
  onNavigate,
}: {
  assetId: string;
  /** Called when an album link is followed, so the viewer can stand down. */
  onNavigate: () => void;
}) {
  const info = useAssetInfo(assetId, true);
  const albums = useAssetAlbums(assetId, true);

  const exif = info.data?.exifInfo;

  // A day's tolerance: mtime and capture time drift by seconds on a normal
  // import, and only a real gap means the file was fetched long after the
  // moment it records.
  const downloadedAt = (() => {
    const taken = info.data?.fileCreatedAt;
    const modified = info.data?.fileModifiedAt;
    if (!taken || !modified) return null;
    const gap = Math.abs(new Date(modified).getTime() - new Date(taken).getTime());
    return gap > 24 * 60 * 60 * 1000 ? modified : null;
  })();
  const location = [exif?.city, exif?.state, exif?.country].filter(Boolean).join(', ');
  const coordinates =
    exif?.latitude && exif?.longitude
      ? `${exif.latitude.toFixed(5)}, ${exif.longitude.toFixed(5)}`
      : null;

  return (
    <aside
      aria-label="Photo information"
      className="scrollbar-subtle w-80 shrink-0 overflow-y-auto border-l border-white/10 bg-black/60 px-4 py-4 text-white/80"
    >
      <Section title="File">
        {/* The timeline's columnar API carries no filename — it is built for
            scrolling 1.6M assets, not for naming one — so there is no cheap
            placeholder to show first. This waits for the asset request. */}
        <Row label="Name">
          <span className="break-all">
            {info.data?.originalFileName ?? (info.isPending ? 'Loading…' : '—')}
          </span>
        </Row>
        <Row label="Size">{formatBytes(exif?.fileSizeInByte)}</Row>
        <Row label="Dimensions">
          {formatDimensions(exif?.exifImageWidth, exif?.exifImageHeight) ?? '—'}
        </Row>
        <Row label="Taken">{formatDateTime(exif?.dateTimeOriginal ?? info.data?.fileCreatedAt)}</Row>

        {/* TWO dates matter for downloaded-profile assets, and they live in
            different columns.
              fileCreatedAt  - when the photo was TAKEN. For these assets it was
                               originally the download date (no usable EXIF, so
                               extraction fell back to file mtime); the repair in
                               scripts/Repair-DownloadedDates.ps1 rewrote it from
                               the epoch embedded in the filename.
              fileModifiedAt - the file's mtime, i.e. when it was DOWNLOADED.
                               Deliberately never rewritten: library scans compare
                               mtime against it to decide whether to re-extract
                               metadata, so changing it would make every rescan
                               silently undo the repair. That refusal is also what
                               preserved this date.
            Shown only when the two genuinely differ - on a normal camera photo
            they are the same instant and a second identical row is noise. */}
        {downloadedAt && <Row label="Downloaded">{formatDateTime(downloadedAt)}</Row>}
      </Section>

      {/* Camera and lens only appear when there is something to say. A phone
          screenshot has no aperture, and four dashes in a row is noise
          pretending to be data. */}
      {(exif?.make || exif?.model || formatLens(exif ?? {})) && (
        <Section title="Camera">
          {(exif?.make || exif?.model) && (
            <Row label="Body">{[exif?.make, exif?.model].filter(Boolean).join(' ')}</Row>
          )}
          {exif?.lensModel && <Row label="Lens">{exif.lensModel}</Row>}
          {formatLens(exif ?? {}) && <Row label="Settings">{formatLens(exif ?? {})}</Row>}
        </Section>
      )}

      {(location || coordinates) && (
        <Section title="Location">
          {location && <Row label="Place">{location}</Row>}
          {coordinates && <Row label="Coordinates">{coordinates}</Row>}
        </Section>
      )}

      <Section title={albums.data && albums.data.length > 1 ? 'Albums' : 'Album'}>
        {albums.isPending && <p className="text-xs text-white/40">Loading…</p>}
        {albums.isSuccess && albums.data.length === 0 && (
          <p className="text-xs text-white/40">Not in any album.</p>
        )}
        {albums.isError && <p className="text-xs text-white/40">Could not load albums.</p>}
        {(albums.data ?? []).map((album) => (
          <AlbumSummary key={album.id} album={album} onNavigate={onNavigate} />
        ))}
      </Section>

      {info.isError && (
        <p className="mt-2 text-xs text-white/40">
          Could not load this photo&rsquo;s metadata.
        </p>
      )}
    </aside>
  );
}

/**
 * One album: its name as a link, then its true size.
 *
 * The name is the clickable part — following it leaves the viewer for the
 * album, which is the natural next move once you have realised where a photo
 * lives. `onNavigate` lets the viewer close itself first rather than being
 * torn down mid-transition.
 *
 * The size comes from the sidecar, which is the only thing that can produce
 * it, and arrives a beat after the name. The counts are shown separately from
 * the total because "18,907 items" and "18,907 photos, no video" are different
 * facts, and which one you needed depends on why you asked.
 */
function AlbumSummary({
  album,
  onNavigate,
}: {
  album: AlbumResponseDto;
  onNavigate: () => void;
}) {
  const stats = useAlbumStats(album.id, true);

  return (
    <div className="mt-2 first:mt-0">
      <Link
        to="/albums/$albumId"
        params={{ albumId: album.id }}
        onClick={onNavigate}
        className="block truncate text-sm font-medium text-accent underline-offset-2 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        title={`Open “${album.albumName}”`}
      >
        {album.albumName}
      </Link>

      <p className="mt-0.5 text-xs tabular-nums text-white/50">
        {album.assetCount.toLocaleString()} {album.assetCount === 1 ? 'item' : 'items'}
        {stats.isSuccess && ` · ${formatBytes(stats.data.bytes)}`}
      </p>

      {stats.isSuccess && (
        <p className="text-xs tabular-nums text-white/40">
          {stats.data.images.toLocaleString()} {stats.data.images === 1 ? 'image' : 'images'}
          {' · '}
          {stats.data.videos.toLocaleString()} {stats.data.videos === 1 ? 'video' : 'videos'}
        </p>
      )}

      {stats.isError && (
        <p className="text-xs text-white/40">Size unavailable — cover sidecar is down.</p>
      )}
    </div>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="mb-5 last:mb-0">
      <h3 className="mb-1.5 text-[11px] font-semibold uppercase tracking-wider text-white/40">
        {title}
      </h3>
      {children}
    </section>
  );
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex gap-2 py-0.5 text-xs">
      <span className="w-24 shrink-0 text-white/40">{label}</span>
      <span className="min-w-0 flex-1 text-white/80">{children}</span>
    </div>
  );
}
