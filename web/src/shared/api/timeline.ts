import type { AssetOrder, AssetVisibility } from '@immich/sdk';
import { sessionHeaders } from './client';

/**
 * Timeline endpoints, wrapped by hand.
 *
 * Everything else calls `@immich/sdk` directly. These two are the exception,
 * for a reason worth understanding because you meet it whenever a generated
 * client lags a server.
 *
 * This deployment adds an `assetType` parameter to the timeline endpoints — a
 * local patch in the forked server. The published SDK is generated from stock
 * Immich and knows nothing about it. Three options:
 *
 *   1. Fork and regenerate the SDK.       Correct, but couples every frontend
 *                                         build to the server monorepo.
 *   2. Cast the SDK call to `any`.        Fast, and discards the type safety
 *                                         that justified the generated client.
 *   3. Wrap the divergent endpoints.      What we do.
 *
 * Small, honest about where local and upstream differ, and deletable the moment
 * upstream catches up.
 */

/** Mirrors the server's `AssetType`. Local addition. */
export type TimelineAssetType = 'IMAGE' | 'VIDEO';

export interface TimeBucketQuery {
  albumId?: string;
  personId?: string;
  tagId?: string;
  userId?: string;
  isFavorite?: boolean;
  /** `true` lists trashed assets — how the Trash page reuses the timeline. */
  isTrashed?: boolean;
  /** 'asc' | 'desc'. Orders the bucket index AND assets within each bucket. */
  order?: AssetOrder;
  visibility?: AssetVisibility;
  withPartners?: boolean;
  withStacked?: boolean;
  /** LOCAL PATCH — not present in stock Immich. */
  assetType?: TimelineAssetType;
}

export interface TimeBucket {
  /** ISO date of the first day of the month, e.g. "2025-10-01". */
  timeBucket: string;
  count: number;
}

/**
 * The server returns buckets COLUMNAR — one array per field, all the same
 * length — rather than an array of objects.
 *
 * For 3,355 assets that avoids repeating seventeen key names 3,355 times,
 * which is a meaningful saving over the wire. The cost is that it is awkward to
 * consume, so `toAssets` below transposes it once at the boundary and the rest
 * of the app never sees the columnar shape.
 *
 * Fields are optional because Immich only sends columns it has data for.
 */
export interface TimeBucketColumns {
  id: string[];
  ratio: number[];
  thumbhash: (string | null)[];
  isImage: boolean[];
  isFavorite?: boolean[];
  isTrashed?: boolean[];
  duration?: (number | string | null)[];
  fileCreatedAt: string[];
  localOffsetHours?: number[];
  livePhotoVideoId?: (string | null)[];
  city?: (string | null)[];
  country?: (string | null)[];
  ownerId?: string[];
  projectionType?: (string | null)[];
  visibility?: string[];
  status?: string[];
  createdAt?: string[];
}

/** One asset, as the rest of the app wants to think about it. */
export interface TimelineAsset {
  id: string;
  /** width / height. Drives the justified layout without measuring images. */
  ratio: number;
  /** Base64 thumbhash — a ~28 byte blurred placeholder. */
  thumbhash: string | null;
  isImage: boolean;
  isFavorite: boolean;
  /** Milliseconds. Only meaningful for video. */
  durationMs: number | null;
  fileCreatedAt: string;
  livePhotoVideoId: string | null;
  city: string | null;
  country: string | null;
}

/**
 * Adapt a full `AssetResponseDto` to the timeline's lean shape.
 *
 * Search results, deep links and any other full-DTO source all funnel through
 * here, so the viewer only ever understands ONE asset shape. The adapter lives
 * at the boundary with the other shape conversions, not inside a component.
 */
export function assetToTimelineAsset(asset: {
  id: string;
  type: string;
  isFavorite: boolean;
  thumbhash?: string | null | undefined;
  /** v3 sends integer milliseconds; v2.7.5 sent "HH:MM:SS.mmm" strings. */
  duration?: number | string | null | undefined;
  fileCreatedAt: string;
  livePhotoVideoId?: string | null | undefined;
  exifInfo?: {
    exifImageWidth?: number | null | undefined;
    exifImageHeight?: number | null | undefined;
    city?: string | null | undefined;
    country?: string | null | undefined;
  } | null | undefined;
}): TimelineAsset {
  return {
    id: asset.id,
    ratio:
      asset.exifInfo?.exifImageWidth && asset.exifInfo?.exifImageHeight
        ? asset.exifInfo.exifImageWidth / asset.exifInfo.exifImageHeight
        : 1,
    thumbhash: asset.thumbhash ?? null,
    isImage: asset.type === 'IMAGE',
    isFavorite: asset.isFavorite,
    durationMs: toDurationMs(asset.duration),
    fileCreatedAt: asset.fileCreatedAt,
    livePhotoVideoId: asset.livePhotoVideoId ?? null,
    city: asset.exifInfo?.city ?? null,
    country: asset.exifInfo?.country ?? null,
  };
}

/**
 * Transpose the columnar payload into objects.
 *
 * Done once per bucket at the API boundary. Doing it lazily per render, or
 * letting the columnar shape leak into components, would trade a clear data
 * model for a saving nobody would notice.
 */
/**
 * Normalise `duration` to milliseconds.
 *
 * The two server versions disagree on the wire type. v2.7.5 sends an
 * "HH:MM:SS.mmm" STRING; v3 sends integer milliseconds. Both are reachable from
 * this app - the dev proxy defaults to v2.7.5 and switches to v3 via IMMICH_URL -
 * so the divergence is resolved here, at the boundary, and everything downstream
 * keeps a number-only contract.
 *
 * Doing it in formatDuration instead would leave `durationMs` lying about its
 * type for every other consumer.
 */
export function toDurationMs(value: number | string | null | undefined): number | null {
  if (value === null || value === undefined) return null;
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;

  const match = /^(\d+):(\d{2}):(\d{2})(?:\.(\d{1,3}))?$/.exec(value.trim());
  if (!match) return null;

  const [, hours, minutes, seconds, fraction] = match;
  const millis = Number((fraction ?? '0').padEnd(3, '0'));
  return ((Number(hours) * 60 + Number(minutes)) * 60 + Number(seconds)) * 1000 + millis;
}

export function toAssets(columns: TimeBucketColumns): TimelineAsset[] {
  const length = columns.id.length;
  const assets: TimelineAsset[] = new Array(length);

  for (let i = 0; i < length; i++) {
    assets[i] = {
      id: columns.id[i] ?? '',
      ratio: columns.ratio[i] ?? 1,
      thumbhash: columns.thumbhash?.[i] ?? null,
      isImage: columns.isImage[i] ?? true,
      isFavorite: columns.isFavorite?.[i] ?? false,
      durationMs: toDurationMs(columns.duration?.[i]),
      fileCreatedAt: columns.fileCreatedAt[i] ?? '',
      livePhotoVideoId: columns.livePhotoVideoId?.[i] ?? null,
      city: columns.city?.[i] ?? null,
      country: columns.country?.[i] ?? null,
    };
  }

  return assets;
}

function toSearchParams(query: object): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && value !== '') {
      params.set(key, String(value));
    }
  }
  return params.toString();
}

async function getJson<T>(path: string): Promise<T> {
  // The active account's token, not just the cookie: see sessionHeaders.
  const response = await fetch(path, { credentials: 'include', headers: sessionHeaders() });
  if (!response.ok) {
    throw new Error(`${response.status} ${response.statusText} — ${path}`);
  }
  return (await response.json()) as T;
}

/**
 * Fetch the bucket index — one row per month with a count.
 *
 * This is what makes a 1.6M-asset timeline tractable. The client learns the
 * shape of the whole library in a single small response, can compute total
 * scroll height immediately, and then fetches only the months actually on
 * screen.
 */
export function getTimeBuckets(query: TimeBucketQuery = {}): Promise<TimeBucket[]> {
  return getJson(`/api/timeline/buckets?${toSearchParams(query)}`);
}

/** Fetch one month of assets. Called lazily as the user scrolls. */
export async function getTimeBucket(
  timeBucket: string,
  query: TimeBucketQuery = {},
): Promise<TimelineAsset[]> {
  const columns = await getJson<TimeBucketColumns>(
    `/api/timeline/bucket?${toSearchParams({ ...query, timeBucket })}`,
  );
  return toAssets(columns);
}
