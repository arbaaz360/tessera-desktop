/**
 * The JSON the core answers, written down once.
 *
 * These used to come from `@immich/sdk`, generated against a server this app
 * no longer talks to. The desktop core keeps the Immich-shaped routes the app
 * already called (contract §3) and adds its own (contract §4); both halves are
 * typed here, by hand, to exactly what the core sends and nothing more. A
 * field that is not in the core's answer is not in these types - an optional
 * field the server never fills is a lie waiting to be rendered as a dash.
 *
 * Source of truth: `docs/CONTRACT.md` for §4, and the fork's
 * `ImmichCompatEndpoints.cs` for the §3 shapes.
 */

// ------------------------------------------------------------ §3: Immich-shaped

/** `order` on the timeline endpoints. Orders buckets and the assets in them. */
export type AssetOrder = 'asc' | 'desc';

/** The one visibility the core knows. Sent because the app always sent it. */
export type AssetVisibility = 'timeline';

export type AssetType = 'IMAGE' | 'VIDEO';

/** `GET /api/albums`, `GET /api/albums/{id}`, and what the mutations return. */
export interface AlbumDto {
  id: string;
  albumName: string;
  description: string;
  ownerId: string;
  /** The picture shown as the cover, or null for an empty album. */
  albumThumbnailAssetId: string | null;
  createdAt: string;
  updatedAt: string;
  assetCount: number;
  shared: boolean;
  hasSharedLink: boolean;
  isActivityEnabled: boolean;
  albumUsers: unknown[];
  assets: unknown[];
  order: AssetOrder;
}

/** `GET /api/assets/{id}`. One picture, with the little metadata the core holds. */
export interface AssetDto {
  id: string;
  ownerId: string;
  type: AssetType;
  /** The file's full path - root plus relative path - or "" if no copy is live. */
  originalPath: string;
  originalFileName: string;
  originalMimeType: string;
  createdAt: string;
  /** When the picture was taken (EXIF when known, else when first seen). */
  fileCreatedAt: string;
  /** The file's mtime. */
  fileModifiedAt: string;
  updatedAt: string;
  localDateTime: string;
  /** Milliseconds, videos only. */
  duration: number | null;
  isFavorite: boolean;
  isArchived: boolean;
  isTrashed: boolean;
  isOffline: boolean;
  isEdited: boolean;
  visibility: AssetVisibility;
  /** Base64 of the sha256 - the file's identity. */
  checksum: string;
  thumbhash: string | null;
  livePhotoVideoId: string | null;
  people: unknown[];
  tags: unknown[];
  exifInfo: {
    fileSizeInByte: number;
    exifImageWidth: number | null;
    exifImageHeight: number | null;
    dateTimeOriginal: string;
  };
}

/** One row of a bulk answer (`PUT /api/albums/{id}/assets`). */
export interface BulkIdResponseDto {
  id: string;
  success: boolean;
  error: string | null;
}

/** `GET /api/albums/overlap`: albums sharing a name with another album. */
export interface OverlapRow {
  name: string;
  mine: { albumId: string; images: number; videos: number };
  other: { ownerId: string; albumId: string; images: number; videos: number };
}

// ------------------------------------------------------------ §4: the core's own

export interface RootCounts {
  files: number;
  contents: number;
  trashed: number;
}

export interface LastScan {
  startedAt: string;
  finishedAt: string | null;
  seen: number;
  added: number;
  moved: number;
  gone: number;
  errors: number;
}

export interface LibraryRoot {
  id: number;
  path: string;
  label: string;
  enabled: boolean;
  counts: RootCounts;
  lastScan: LastScan | null;
  scanning: boolean;
}

export type LicenceState = 'free' | 'licensed';

export interface Licence {
  state: LicenceState;
  cap: number;
  indexed: number;
  capReached: boolean;
}

/** `GET /api/library`: the whole library at a glance, in numbers. */
export interface Library {
  roots: LibraryRoot[];
  thumbnails: { pending: number; done: number };
  embeddings: { pending: number; done: number; model: string; ready: boolean };
  licence: Licence;
  watch: { running: boolean; lastTick: string | null };
}

export interface DuplicateLocation {
  id: number;
  rootId: number;
  relPath: string;
}

/** `GET /api/library/duplicates`: one content with two or more live files. */
export interface DuplicateSet {
  contentId: number;
  sha256: string;
  byteSize: number;
  locations: DuplicateLocation[];
}

export type SearchDevice = 'cpu' | 'directml' | 'coreml';

/**
 * `GET /api/search/status`. The core leaves null fields out of the JSON, so
 * `downloading`, `waiting` and `error` arrive as absent as often as null;
 * read them with `!= null`.
 */
export interface SearchStatus {
  model: string;
  ready: boolean;
  downloading?: { receivedBytes: number; totalBytes: number; file: string } | null;
  embedded: number;
  total: number;
  pending?: number;
  failed?: number;
  enabled: boolean;
  /** Meaningful once `ready`; before that the core reports its fallback. */
  device: SearchDevice;
  /** Why the embedder is idle, in the core's words. */
  waiting?: string | null;
  /** Why the last attempt to make the model ready failed. */
  error?: string | null;
}

/**
 * One search hit: a row of `/api/timeline/bucket` turned into an object, plus
 * a score. See `SearchAnswer` in `features/search/api.ts` for the envelope.
 */
export interface SearchItem {
  id: string;
  ownerId: string;
  ratio: number;
  isFavorite: boolean;
  visibility: string;
  isTrashed: boolean;
  isImage: boolean;
  thumbhash: string | null;
  createdAt: string;
  fileCreatedAt: string;
  localOffsetHours: number;
  duration: number | null;
  projectionType: string | null;
  livePhotoVideoId: string | null;
  score: number;
}

/** `GET /api/settings` and the answer to `PUT /api/settings`. */
export interface Settings {
  thumbnailConcurrency: number;
  embedOnBattery: boolean;
  version: string;
  dataDir: string;
}

export type SettingsPatch = Partial<Pick<Settings, 'thumbnailConcurrency' | 'embedOnBattery'>>;
