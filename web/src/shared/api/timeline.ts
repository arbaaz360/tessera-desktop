import { apiJson } from './tessera';
import type { AssetOrder, AssetVisibility, SearchItem } from './types';

/**
 * The timeline endpoints: the bucket index and one month of assets.
 *
 * Every view of the library is the timeline with a filter - the main grid, an
 * album, the trash, photos-only, oldest-first - so these two calls carry the
 * whole product's browsing. Their query parameter names are frozen by the
 * contract (§3); the core answers them unchanged from the fork.
 */

export type TimelineAssetType = 'IMAGE' | 'VIDEO';

export interface TimeBucketQuery {
  albumId?: string;
  isFavorite?: boolean;
  /** `true` lists trashed assets - how the Trash page reuses the timeline. */
  isTrashed?: boolean;
  /** Orders the bucket index AND assets within each bucket. */
  order?: AssetOrder;
  visibility?: AssetVisibility;
  withPartners?: boolean;
  /** Photos or videos only. The core filters in SQL, not the browser. */
  assetType?: TimelineAssetType;
}

export interface TimeBucket {
  /** ISO date of the first day of the month, e.g. "2025-10-01". */
  timeBucket: string;
  count: number;
}

/**
 * The core returns a bucket COLUMNAR - one array per field, all the same
 * length - rather than an array of objects. For a month of 3,000 pictures
 * that avoids repeating fourteen key names 3,000 times. `toAssets` transposes
 * it once at the boundary and the rest of the app never sees this shape.
 *
 * Fields are optional because the core only sends columns it has data for.
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
  ownerId?: string[];
  projectionType?: (string | null)[];
  visibility?: string[];
  createdAt?: string[];
}

/** One asset, as the rest of the app wants to think about it. */
export interface TimelineAsset {
  id: string;
  /** width / height. Drives the justified layout without measuring images. */
  ratio: number;
  /** Base64 thumbhash - a ~28 byte blurred placeholder. Null until the core makes them. */
  thumbhash: string | null;
  isImage: boolean;
  isFavorite: boolean;
  /** Milliseconds. Only meaningful for video. */
  durationMs: number | null;
  fileCreatedAt: string;
  livePhotoVideoId: string | null;
}

/**
 * Adapt a full asset (`GET /api/assets/{id}`) to the timeline's lean shape.
 *
 * Deep links and any other full-asset source funnel through here, so the
 * viewer only ever understands ONE asset shape.
 */
export function assetToTimelineAsset(asset: {
  id: string;
  type: string;
  isFavorite: boolean;
  thumbhash?: string | null | undefined;
  duration?: number | string | null | undefined;
  fileCreatedAt: string;
  livePhotoVideoId?: string | null | undefined;
  exifInfo?: {
    exifImageWidth?: number | null | undefined;
    exifImageHeight?: number | null | undefined;
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
  };
}

/**
 * A search hit is one row of the bucket shape as an object (contract §4), so
 * the grid and the viewer render it like anything else.
 */
export function searchItemToTimelineAsset(item: SearchItem): TimelineAsset {
  return {
    id: item.id,
    ratio: item.ratio,
    thumbhash: item.thumbhash ?? null,
    isImage: item.isImage,
    isFavorite: item.isFavorite,
    durationMs: toDurationMs(item.duration),
    fileCreatedAt: item.fileCreatedAt,
    livePhotoVideoId: item.livePhotoVideoId ?? null,
  };
}

/**
 * Normalise `duration` to milliseconds.
 *
 * The core sends integer milliseconds. An older server sent "HH:MM:SS.mmm"
 * strings; the parser stays because it costs nothing and a number-only
 * contract downstream is worth keeping honest at the boundary.
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

/** Transpose the columnar payload into objects, once per bucket. */
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
    };
  }

  return assets;
}

export function toSearchParams(query: object): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && value !== '') {
      params.set(key, String(value));
    }
  }
  return params.toString();
}

/**
 * Fetch the bucket index - one row per month with a count.
 *
 * This is what makes a 200,000-picture library tractable: the client learns
 * the shape of the whole library in a single small response, computes the
 * total scroll height, and then fetches only the months on screen.
 */
export function getTimeBuckets(query: TimeBucketQuery = {}): Promise<TimeBucket[]> {
  return apiJson(`/api/timeline/buckets?${toSearchParams(query)}`);
}

/** Fetch one month of assets. Called lazily as the user scrolls. */
export async function getTimeBucket(
  timeBucket: string,
  query: TimeBucketQuery = {},
): Promise<TimelineAsset[]> {
  const columns = await apiJson<TimeBucketColumns>(
    `/api/timeline/bucket?${toSearchParams({ ...query, timeBucket })}`,
  );
  return toAssets(columns);
}
