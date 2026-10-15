import type { ReactNode } from 'react';
import { Link } from '@tanstack/react-router';
import type { AlbumDto } from '@/shared/api/types';
import { formatBytes, formatDateTime } from '@/shared/format';
import { formatDimensions, useAssetAlbums, useAssetInfo } from './info';

/**
 * The viewer's info sidebar.
 *
 * Answers the questions you have while looking AT a picture rather than at
 * the grid: what file is this, where on disk does it live, how big is it, and
 * which albums is it in.
 *
 * Sits BESIDE the image, not over it. An overlay panel would cover the thing
 * being described; the image area simply gets narrower while the panel is
 * open. The two requests degrade independently: a picture in no album shows
 * its file section and says "not in any album", and nothing else changes.
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

  // The file's mtime beside the taken date, only when they genuinely differ:
  // a day's tolerance absorbs the seconds of drift a normal import has, and a
  // real gap means the file was fetched long after the moment it records.
  const fileDate = (() => {
    const taken = info.data?.fileCreatedAt;
    const modified = info.data?.fileModifiedAt;
    if (!taken || !modified) return null;
    const gap = Math.abs(new Date(modified).getTime() - new Date(taken).getTime());
    return gap > 24 * 60 * 60 * 1000 ? modified : null;
  })();

  return (
    <aside
      aria-label="Picture information"
      className="scrollbar-subtle w-80 shrink-0 overflow-y-auto border-l border-white/10 bg-black/60 px-4 py-4 text-white/80"
    >
      <Section title="File">
        {/* The timeline's columnar API carries no file name - it is built for
            scrolling, not for naming one - so this waits for the asset request. */}
        <Row label="Name">
          <span className="break-all">
            {info.data?.originalFileName ?? (info.isPending ? 'Loading…' : '—')}
          </span>
        </Row>
        {info.data?.originalPath && (
          <Row label="Where">
            <span className="break-all font-mono text-[11px]" title={info.data.originalPath}>
              {info.data.originalPath}
            </span>
          </Row>
        )}
        <Row label="Size">{formatBytes(exif?.fileSizeInByte)}</Row>
        <Row label="Dimensions">
          {formatDimensions(exif?.exifImageWidth, exif?.exifImageHeight) ?? '—'}
        </Row>
        <Row label="Taken">{formatDateTime(exif?.dateTimeOriginal ?? info.data?.fileCreatedAt)}</Row>
        {fileDate && <Row label="File date">{formatDateTime(fileDate)}</Row>}
        {info.data?.isTrashed && <Row label="Status">In the trash</Row>}
      </Section>

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
        <p className="mt-2 text-xs text-white/40">Could not load this picture&rsquo;s details.</p>
      )}
    </aside>
  );
}

/**
 * One album: its name as a link, then its size. Following the name leaves the
 * viewer for the album, which is the natural next move once you have realised
 * where a picture lives.
 */
function AlbumSummary({ album, onNavigate }: { album: AlbumDto; onNavigate: () => void }) {
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
      </p>
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
