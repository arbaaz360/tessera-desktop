#!/usr/bin/env node
/**
 * A stand-in for tessera-core, so `pnpm dev` works before the real core does.
 *
 * Implements every route the web app calls (docs/CONTRACT.md §3 and §4) over
 * fake data: a few hundred generated pictures per folder, coloured SVG
 * placeholders for thumbnails and originals, a scan that "discovers" files
 * over a few seconds, a model download that takes ten seconds, thumbnails
 * and embeddings that drain at a visible pace, and the free-tier cap.
 *
 * No dependencies. Node 20+.
 *
 *   node mock/server.mjs            # port 2501, token from .env.example
 *   MOCK_RESET=1 node mock/server.mjs   # forget the saved state, start empty
 *
 * State (roots, albums, favourites, trash, licence) persists in
 * mock/.state.json so a dev-server restart does not mean onboarding again.
 * Pictures are generated deterministically from the root id, so ids stay
 * stable across restarts.
 */

import { createServer } from 'node:http';
import { readFileSync, writeFileSync, existsSync, unlinkSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));

// ------------------------------------------------------------------ config

const PORT = Number(process.env.MOCK_PORT ?? 2501);
const TOKEN = process.env.MOCK_TOKEN ?? 'dev-token-not-for-production';
/** Free-tier cap. Small, so a few hundred pictures reach it. */
const CAP = Number(process.env.MOCK_CAP ?? 200);
/** Pictures generated per root. */
const ASSETS_PER_ROOT = Number(process.env.MOCK_ASSETS ?? 320);
/** How long /health/ready answers 503 after start, so the boot screen shows. */
const READY_AFTER_MS = Number(process.env.MOCK_SLOW_START ?? 1500);
const MODEL = 'siglip2-base-patch16-256';
const MODEL_BYTES = 190 * 1024 * 1024;
const DOWNLOAD_MS = 10_000;
const TICK_MS = 500;
const STATE_FILE = join(here, '.state.json');

// ------------------------------------------------------------------- state

const startedAt = Date.now();

/** @type {{
 *   roots: Array<{ id: number; path: string; label: string; enabled: boolean; addedAt: string; discovered: number; scanning: boolean; lastScan: object | null; runs: number }>;
 *   nextRootId: number;
 *   manualAlbums: Array<{ id: string; name: string; description: string; createdAt: string; updatedAt: string; members: string[] }>;
 *   nextAlbumId: number;
 *   deletedAlbums: string[];
 *   covers: Record<string, string>;
 *   albumNames: Record<string, string>;
 *   favourites: string[];
 *   trashed: string[];
 *   licence: 'free' | 'licensed';
 *   searchEnabled: boolean;
 *   modelReady: boolean;
 *   settings: { thumbnailConcurrency: number; embedOnBattery: boolean };
 * }} */
let state = {
  roots: [],
  nextRootId: 1,
  manualAlbums: [],
  nextAlbumId: 1,
  deletedAlbums: [],
  covers: {},
  albumNames: {},
  favourites: [],
  trashed: [],
  licence: 'free',
  searchEnabled: true,
  modelReady: false,
  settings: { thumbnailConcurrency: 4, embedOnBattery: false },
};

if (process.env.MOCK_RESET === '1' && existsSync(STATE_FILE)) unlinkSync(STATE_FILE);
if (existsSync(STATE_FILE)) {
  try {
    state = { ...state, ...JSON.parse(readFileSync(STATE_FILE, 'utf8')) };
  } catch {
    /* a corrupt state file is just a fresh start */
  }
}

function save() {
  writeFileSync(STATE_FILE, JSON.stringify(state, null, 2));
}

// In memory only: the work queues and the download.
let thumbsDone = 0;
let embedsDone = 0;
/** @type {{ startedAt: number } | null} */
let download = null;
let watchTick = Date.now();

// -------------------------------------------------------------- generation

function mulberry32(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function hashString(input) {
  let h = 2166136261;
  for (let i = 0; i < input.length; i++) {
    h ^= input.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return h >>> 0;
}

const FOLDERS = [
  'sketches',
  'references/anatomy',
  'references/hands',
  'references/2023',
  'photos/2023',
  'inspiration',
];

const NOUNS = ['horse', 'hand', 'portrait', 'street', 'tree', 'dress', 'cat', 'boat', 'window', 'cloud'];
const STYLES = ['ink', 'pencil', 'photo', 'watercolour', 'charcoal', 'study'];

/** Every picture of a root, deterministic from the root id. Cached. */
const assetCache = new Map();
function assetsOf(root) {
  if (assetCache.has(root.id)) return assetCache.get(root.id);
  const rnd = mulberry32(root.id * 7919 + 17);
  const list = [];
  const now = Date.now();
  for (let i = 0; i < ASSETS_PER_ROOT; i++) {
    const n = i + 1;
    const id = `r${root.id}-${String(n).padStart(5, '0')}`;
    const isImage = rnd() > 0.08;
    const folder = FOLDERS[i % FOLDERS.length];
    const style = STYLES[Math.floor(rnd() * STYLES.length)];
    const noun = NOUNS[Math.floor(rnd() * NOUNS.length)];
    const ext = isImage ? (rnd() > 0.5 ? 'jpg' : 'png') : 'mp4';
    const name = `${style}-${noun}-${String(n).padStart(4, '0')}.${ext}`;
    const width = 800 + Math.floor(rnd() * 1600);
    const height = 600 + Math.floor(rnd() * 1600);
    // Spread over the last 24 months, newest first-ish.
    const monthsAgo = Math.floor((i / ASSETS_PER_ROOT) * 24);
    const taken = new Date(now - monthsAgo * 30.4 * 86400_000 - Math.floor(rnd() * 25) * 86400_000);
    const hue = Math.floor(rnd() * 360);
    list.push({
      id,
      ordinalInRoot: i,
      rootId: root.id,
      relPath: `${folder}/${name}`,
      folder,
      name,
      isImage,
      mime: isImage ? (ext === 'png' ? 'image/png' : 'image/jpeg') : 'video/mp4',
      width,
      height,
      durationMs: isImage ? null : 3_000 + Math.floor(rnd() * 120_000),
      takenAt: taken.toISOString(),
      byteSize: 40_000 + Math.floor(rnd() * 6_000_000),
      sha256: hashString(id).toString(16).padStart(8, '0').repeat(8),
      hue,
      // Every 37th picture exists twice on disk.
      duplicate: i % 37 === 5,
    });
  }
  assetCache.set(root.id, list);
  return list;
}

/** Pictures the scanner has found so far, across all roots, in queue order. */
function discoveredAssets() {
  const out = [];
  for (const root of state.roots) {
    const all = assetsOf(root);
    for (let i = 0; i < root.discovered && i < all.length; i++) out.push(all[i]);
  }
  return out;
}

const byId = () => new Map(discoveredAssets().map((a) => [a.id, a]));

/** Position in the global work queue. Thumbnails and embeddings drain in this order. */
function ordinalOf(asset) {
  let ordinal = 0;
  for (const root of state.roots) {
    if (root.id === asset.rootId) return ordinal + asset.ordinalInRoot;
    ordinal += Math.min(root.discovered, ASSETS_PER_ROOT);
  }
  return ordinal;
}

const licensed = () => state.licence === 'licensed';
/** How many pictures may get a thumbnail and a vector right now. */
function eligibleCount() {
  const found = discoveredAssets().length;
  return licensed() ? found : Math.min(found, CAP);
}
const hasThumb = (asset) => ordinalOf(asset) < thumbsDone;
const hasEmbedding = (asset) => asset.isImage && ordinalOf(asset) < embedsDone;

// -------------------------------------------------------------------- tick

setInterval(() => {
  let changed = false;
  for (const root of state.roots) {
    if (root.scanning) {
      root.discovered = Math.min(ASSETS_PER_ROOT, root.discovered + 45);
      if (root.discovered >= ASSETS_PER_ROOT) {
        root.scanning = false;
        const finished = new Date().toISOString();
        const first = root.runs === 1;
        root.lastScan = {
          startedAt: root.lastScan?.startedAt ?? finished,
          finishedAt: finished,
          seen: ASSETS_PER_ROOT + assetsOf(root).filter((a) => a.duplicate).length,
          added: first ? ASSETS_PER_ROOT : 0,
          moved: first ? 0 : 2,
          gone: 0,
          errors: 0,
        };
        changed = true;
      }
    }
  }
  const eligible = eligibleCount();
  thumbsDone = Math.min(eligible, thumbsDone + 12);
  if (state.modelReady && state.searchEnabled) embedsDone = Math.min(eligible, embedsDone + 8);

  if (download && Date.now() - download.startedAt >= DOWNLOAD_MS) {
    download = null;
    state.modelReady = true;
    changed = true;
  }
  watchTick = Date.now();
  if (changed) save();
}, TICK_MS);

// ------------------------------------------------------------ HTTP helpers

function json(res, status, body) {
  const text = body === undefined ? '' : JSON.stringify(body);
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(text),
  });
  res.end(text);
}

const problem = (res, status, detail) => json(res, status, { detail });

async function readBody(req) {
  const chunks = [];
  for await (const chunk of req) chunks.push(chunk);
  if (chunks.length === 0) return {};
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'));
  } catch {
    return {};
  }
}

function cors(req, res) {
  res.setHeader('access-control-allow-origin', req.headers.origin ?? '*');
  res.setHeader('access-control-allow-methods', 'GET, POST, PUT, PATCH, DELETE, OPTIONS');
  res.setHeader('access-control-allow-headers', 'authorization, content-type');
  res.setHeader('access-control-max-age', '600');
}

function authorised(req, url) {
  const header = req.headers.authorization;
  if (header === `Bearer ${TOKEN}`) return true;
  return url.searchParams.get('sessionKey') === TOKEN;
}

const iso = (d) => new Date(d).toISOString();
const bool = (v) => v === 'true';

// -------------------------------------------------------------- the shapes

function assetDto(asset) {
  const root = state.roots.find((r) => r.id === asset.rootId);
  return {
    id: asset.id,
    ownerId: 'account-1',
    type: asset.isImage ? 'IMAGE' : 'VIDEO',
    originalPath: root ? `${root.path.replace(/[\\/]+$/, '')}/${asset.relPath}` : '',
    originalFileName: asset.name,
    originalMimeType: asset.mime,
    createdAt: asset.takenAt,
    fileCreatedAt: asset.takenAt,
    fileModifiedAt: asset.takenAt,
    updatedAt: asset.takenAt,
    localDateTime: asset.takenAt,
    duration: asset.durationMs,
    isFavorite: state.favourites.includes(asset.id),
    isArchived: false,
    isTrashed: state.trashed.includes(asset.id),
    isOffline: false,
    isEdited: false,
    visibility: 'timeline',
    checksum: Buffer.from(asset.sha256, 'hex').toString('base64'),
    thumbhash: null,
    livePhotoVideoId: null,
    people: [],
    tags: [],
    exifInfo: {
      fileSizeInByte: asset.byteSize,
      exifImageWidth: asset.width,
      exifImageHeight: asset.height,
      dateTimeOriginal: asset.takenAt,
    },
  };
}

/** One row of the columnar bucket, as an object (the search shape). */
function timelineRow(asset) {
  return {
    id: asset.id,
    ownerId: 'account-1',
    ratio: asset.width / asset.height,
    isFavorite: state.favourites.includes(asset.id),
    visibility: 'timeline',
    isTrashed: state.trashed.includes(asset.id),
    isImage: asset.isImage,
    thumbhash: null,
    createdAt: asset.takenAt,
    fileCreatedAt: asset.takenAt,
    localOffsetHours: 0,
    duration: asset.durationMs,
    projectionType: null,
    livePhotoVideoId: null,
  };
}

function columnar(rows) {
  const pick = (key) => rows.map((r) => r[key]);
  return {
    id: pick('id'),
    ownerId: pick('ownerId'),
    ratio: pick('ratio'),
    isFavorite: pick('isFavorite'),
    visibility: pick('visibility'),
    isTrashed: pick('isTrashed'),
    isImage: pick('isImage'),
    thumbhash: pick('thumbhash'),
    createdAt: pick('createdAt'),
    fileCreatedAt: pick('fileCreatedAt'),
    localOffsetHours: pick('localOffsetHours'),
    duration: pick('duration'),
    projectionType: pick('projectionType'),
    livePhotoVideoId: pick('livePhotoVideoId'),
  };
}

// ------------------------------------------------------------------ albums

const folderAlbumId = (rootId, folder) => `alb-r${rootId}-${folder.replace(/[^a-z0-9]+/gi, '-')}`;

/** Folder albums (one per folder per root) plus manual albums, minus deleted. */
function allAlbums() {
  const albums = [];
  for (const root of state.roots) {
    const assets = assetsOf(root).slice(0, root.discovered);
    for (const folder of FOLDERS) {
      const id = folderAlbumId(root.id, folder);
      const members = assets.filter((a) => a.folder === folder).map((a) => a.id);
      albums.push({
        id,
        name: state.albumNames[id] ?? folder.split('/').pop(),
        description: '',
        createdAt: root.addedAt,
        updatedAt: root.lastScan?.finishedAt ?? root.addedAt,
        members,
        folder: true,
      });
    }
  }
  for (const album of state.manualAlbums) {
    albums.push({ ...album, name: state.albumNames[album.id] ?? album.name, folder: false });
  }
  return albums.filter((a) => !state.deletedAlbums.includes(a.id));
}

function albumDto(album, assets = byId()) {
  const live = album.members.filter((id) => assets.has(id) && !state.trashed.includes(id));
  const cover = state.covers[album.id] ?? live.find((id) => hasThumb(assets.get(id))) ?? live[0] ?? null;
  return {
    id: album.id,
    albumName: album.name,
    description: album.description,
    ownerId: 'account-1',
    albumThumbnailAssetId: cover,
    createdAt: album.createdAt,
    updatedAt: album.updatedAt,
    assetCount: live.length,
    shared: false,
    hasSharedLink: false,
    isActivityEnabled: false,
    albumUsers: [],
    assets: [],
    order: 'desc',
  };
}

function overlapRows() {
  const albums = allAlbums();
  const assets = byId();
  const rows = [];
  for (const mine of albums) {
    for (const other of albums) {
      if (mine.id === other.id || mine.name.toLowerCase() !== other.name.toLowerCase()) continue;
      const count = (album) => {
        const live = album.members.filter((id) => assets.has(id) && !state.trashed.includes(id));
        const images = live.filter((id) => assets.get(id).isImage).length;
        return { images, videos: live.length - images };
      };
      rows.push({
        name: mine.name,
        mine: { albumId: mine.id, ...count(mine) },
        other: { ownerId: 'account-1', albumId: other.id, ...count(other) },
      });
    }
  }
  return rows;
}

// ---------------------------------------------------------------- timeline

function timelineFilter(url) {
  const albumId = url.searchParams.get('albumId');
  const isTrashed = bool(url.searchParams.get('isTrashed'));
  const isFavorite = bool(url.searchParams.get('isFavorite'));
  const assetType = url.searchParams.get('assetType')?.toUpperCase();
  const order = url.searchParams.get('order') === 'asc' ? 'asc' : 'desc';

  let list = discoveredAssets();
  if (albumId) {
    const album = allAlbums().find((a) => a.id === albumId);
    const members = new Set(album?.members ?? []);
    list = list.filter((a) => members.has(a.id));
  }
  list = list.filter((a) => state.trashed.includes(a.id) === isTrashed);
  if (isFavorite) list = list.filter((a) => state.favourites.includes(a.id));
  if (assetType === 'IMAGE') list = list.filter((a) => a.isImage);
  if (assetType === 'VIDEO') list = list.filter((a) => !a.isImage);
  list.sort((a, b) => (a.takenAt < b.takenAt ? -1 : a.takenAt > b.takenAt ? 1 : a.id < b.id ? -1 : 1));
  if (order === 'desc') list.reverse();
  return list;
}

const monthOf = (isoDate) => `${isoDate.slice(0, 7)}-01`;

// ------------------------------------------------------------------ search

function searchFilter(url, list) {
  const type = url.searchParams.get('type')?.toUpperCase();
  const after = url.searchParams.get('takenAfter');
  const before = url.searchParams.get('takenBefore');
  const albumId = url.searchParams.get('albumId');
  if (type === 'IMAGE') list = list.filter((a) => a.isImage);
  if (type === 'VIDEO') list = list.filter((a) => !a.isImage);
  if (after) list = list.filter((a) => a.takenAt >= after);
  if (before) list = list.filter((a) => a.takenAt <= before);
  if (albumId) {
    const members = new Set(allAlbums().find((a) => a.id === albumId)?.members ?? []);
    list = list.filter((a) => members.has(a.id));
  }
  return list.filter((a) => !state.trashed.includes(a.id));
}

const limitOf = (url) => Math.min(500, Math.max(1, Number(url.searchParams.get('limit') ?? 100) || 100));

/** A stable pseudo-score in [0.15, 0.45): words that appear in the file name score higher. */
function scoreFor(query, asset) {
  const base = (hashString(`${query}|${asset.id}`) % 1000) / 1000;
  const bonus = query
    .toLowerCase()
    .split(/\s+/)
    .filter(Boolean)
    .some((word) => asset.name.includes(word))
    ? 0.25
    : 0;
  return Math.round((0.15 + base * 0.3 + bonus) * 1000) / 1000;
}

// -------------------------------------------------------------- placeholders

/** A coloured SVG standing in for a picture: its hue from the id, its shape from the ratio. */
function placeholderSvg(asset, size) {
  const ratio = asset.width / asset.height;
  const w = ratio >= 1 ? size : Math.round(size * ratio);
  const h = ratio >= 1 ? Math.round(size / ratio) : size;
  const hue = asset.hue;
  const label = asset.isImage ? asset.name.replace(/\.\w+$/, '') : `▶ ${asset.name}`;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">
  <defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
    <stop offset="0" stop-color="hsl(${hue} 45% 38%)"/><stop offset="1" stop-color="hsl(${(hue + 40) % 360} 50% 22%)"/>
  </linearGradient></defs>
  <rect width="${w}" height="${h}" fill="url(#g)"/>
  <circle cx="${w * 0.68}" cy="${h * 0.36}" r="${Math.min(w, h) * 0.18}" fill="hsl(${(hue + 180) % 360} 55% 70% / 0.55)"/>
  <path d="M0 ${h} L${w * 0.45} ${h * 0.55} L${w * 0.7} ${h * 0.8} L${w} ${h * 0.5} L${w} ${h} Z" fill="hsl(${hue} 35% 16% / 0.7)"/>
  <text x="${w / 2}" y="${h - Math.max(10, h * 0.06)}" font-family="system-ui, sans-serif" font-size="${Math.max(10, Math.round(Math.min(w, h) * 0.07))}" fill="white" fill-opacity="0.85" text-anchor="middle">${label}</text>
</svg>`;
}

// ---------------------------------------------------------------- routing

const routes = [];
const route = (method, pattern, handler) => routes.push({ method, pattern, handler });

// health (no token)
route('GET', /^\/health\/live$/, (ctx) => json(ctx.res, 200, { status: 'live' }));
route('GET', /^\/health\/ready$/, (ctx) =>
  Date.now() - startedAt >= READY_AFTER_MS
    ? json(ctx.res, 200, { status: 'ready' })
    : problem(ctx.res, 503, 'starting'),
);

// timeline
route('GET', /^\/api\/timeline\/buckets$/, (ctx) => {
  const list = timelineFilter(ctx.url);
  const counts = new Map();
  for (const a of list) counts.set(monthOf(a.takenAt), (counts.get(monthOf(a.takenAt)) ?? 0) + 1);
  const order = ctx.url.searchParams.get('order') === 'asc' ? 1 : -1;
  const buckets = [...counts.entries()]
    .sort(([a], [b]) => (a < b ? -order : order))
    .map(([timeBucket, count]) => ({ timeBucket, count }));
  json(ctx.res, 200, buckets);
});

route('GET', /^\/api\/timeline\/bucket$/, (ctx) => {
  const bucket = ctx.url.searchParams.get('timeBucket');
  if (!bucket || !/^\d{4}-\d{2}-\d{2}$/.test(bucket)) return problem(ctx.res, 400, 'timeBucket must be YYYY-MM-DD');
  const rows = timelineFilter(ctx.url)
    .filter((a) => monthOf(a.takenAt) === bucket)
    .map(timelineRow);
  json(ctx.res, 200, columnar(rows));
});

// assets
route('GET', /^\/api\/assets\/([^/]+)\/thumbnail$/, (ctx, [, id]) => {
  const asset = byId().get(id);
  if (!asset || !hasThumb(asset)) return problem(ctx.res, 404, 'no thumbnail');
  const svg = placeholderSvg(asset, 320);
  ctx.res.writeHead(200, {
    'content-type': 'image/svg+xml',
    'cache-control': 'private, max-age=31536000, immutable',
  });
  ctx.res.end(svg);
});

route('GET', /^\/api\/assets\/([^/]+)\/original$/, (ctx, [, id]) => {
  const asset = byId().get(id);
  if (!asset) return problem(ctx.res, 404, 'not found');
  const svg = placeholderSvg(asset, 1600);
  ctx.res.writeHead(200, {
    'content-type': 'image/svg+xml',
    'content-disposition': `inline; filename="${asset.name.replace(/\.\w+$/, '')}.svg"`,
    'cache-control': 'private, max-age=86400',
  });
  ctx.res.end(svg);
});

route('PUT', /^\/api\/assets\/?$/, async (ctx) => {
  const body = await readBody(ctx.req);
  const ids = Array.isArray(body.ids) ? body.ids : [];
  if (typeof body.isFavorite === 'boolean') {
    const set = new Set(state.favourites);
    for (const id of ids) body.isFavorite ? set.add(id) : set.delete(id);
    state.favourites = [...set];
    save();
  }
  json(ctx.res, 204);
});

route('DELETE', /^\/api\/assets\/?$/, async (ctx) => {
  const body = await readBody(ctx.req);
  if (body.force === true) return problem(ctx.res, 403, 'Originals are never deleted here. Trash hides a picture; restore brings it back.');
  const set = new Set(state.trashed);
  for (const id of Array.isArray(body.ids) ? body.ids : []) set.add(id);
  state.trashed = [...set];
  save();
  json(ctx.res, 204);
});

route('GET', /^\/api\/assets\/([^/]+)$/, (ctx, [, id]) => {
  const asset = byId().get(id);
  if (!asset) return problem(ctx.res, 404, 'not found');
  json(ctx.res, 200, assetDto(asset));
});

// trash
route('POST', /^\/api\/trash\/restore\/assets$/, async (ctx) => {
  const body = await readBody(ctx.req);
  const ids = new Set(Array.isArray(body.ids) ? body.ids : []);
  const before = state.trashed.length;
  state.trashed = state.trashed.filter((id) => !ids.has(id));
  save();
  json(ctx.res, 200, { count: before - state.trashed.length });
});
route('POST', /^\/api\/trash\/restore$/, (ctx) => {
  const count = state.trashed.length;
  state.trashed = [];
  save();
  json(ctx.res, 200, { count });
});
route('POST', /^\/api\/trash\/empty$/, (ctx) =>
  problem(ctx.res, 403, 'Originals are never deleted here. Restore what you want back and leave the rest hidden.'),
);

// albums
route('GET', /^\/api\/albums\/overlap$/, (ctx) => json(ctx.res, 200, overlapRows()));

route('GET', /^\/api\/albums\/?$/, (ctx) => {
  const assets = byId();
  const containing = ctx.url.searchParams.get('assetId');
  let albums = allAlbums();
  if (containing !== null) albums = albums.filter((a) => a.members.includes(containing));
  json(ctx.res, 200, albums.map((a) => albumDto(a, assets)));
});

route('POST', /^\/api\/albums\/?$/, async (ctx) => {
  const body = await readBody(ctx.req);
  const name = typeof body.albumName === 'string' ? body.albumName.trim() : '';
  if (!name) return problem(ctx.res, 400, 'An album needs a name');
  const now = new Date().toISOString();
  const album = {
    id: `alb-m-${state.nextAlbumId++}`,
    name,
    description: typeof body.description === 'string' ? body.description : '',
    createdAt: now,
    updatedAt: now,
    members: Array.isArray(body.assetIds) ? body.assetIds : [],
  };
  state.manualAlbums.push(album);
  save();
  json(ctx.res, 201, albumDto({ ...album, folder: false }));
});

route('GET', /^\/api\/albums\/([^/]+)$/, (ctx, [, id]) => {
  const album = allAlbums().find((a) => a.id === id);
  if (!album) return problem(ctx.res, 404, 'not found');
  json(ctx.res, 200, albumDto(album));
});

route('PATCH', /^\/api\/albums\/([^/]+)$/, async (ctx, [, id]) => {
  const album = allAlbums().find((a) => a.id === id);
  if (!album) return problem(ctx.res, 404, 'not found');
  const body = await readBody(ctx.req);
  if (typeof body.albumName === 'string' && body.albumName.trim()) state.albumNames[id] = body.albumName.trim();
  if (typeof body.albumThumbnailAssetId === 'string' && album.members.includes(body.albumThumbnailAssetId)) {
    state.covers[id] = body.albumThumbnailAssetId;
  }
  const manual = state.manualAlbums.find((a) => a.id === id);
  if (manual) {
    if (typeof body.description === 'string') manual.description = body.description;
    manual.updatedAt = new Date().toISOString();
  }
  save();
  json(ctx.res, 200, albumDto(allAlbums().find((a) => a.id === id)));
});

route('DELETE', /^\/api\/albums\/([^/]+)$/, (ctx, [, id]) => {
  if (!allAlbums().some((a) => a.id === id)) return problem(ctx.res, 404, 'not found');
  state.deletedAlbums.push(id);
  save();
  json(ctx.res, 204);
});

route('PUT', /^\/api\/albums\/([^/]+)\/assets$/, async (ctx, [, id]) => {
  const manual = state.manualAlbums.find((a) => a.id === id);
  const album = allAlbums().find((a) => a.id === id);
  if (!album) return problem(ctx.res, 404, 'not found');
  const body = await readBody(ctx.req);
  const assets = byId();
  const ids = Array.isArray(body.ids) ? body.ids : [];
  const answer = ids.map((raw) => {
    if (!assets.has(raw)) return { id: raw, success: false, error: 'not_found' };
    if (album.members.includes(raw)) return { id: raw, success: false, error: 'duplicate' };
    if (manual) manual.members.push(raw);
    return { id: raw, success: Boolean(manual), error: manual ? null : 'folder_album' };
  });
  if (manual) manual.updatedAt = new Date().toISOString();
  save();
  json(ctx.res, 200, answer);
});

route('DELETE', /^\/api\/albums\/([^/]+)\/assets$/, async (ctx, [, id]) => {
  const manual = state.manualAlbums.find((a) => a.id === id);
  if (!manual) return problem(ctx.res, 404, 'not found');
  const body = await readBody(ctx.req);
  const ids = Array.isArray(body.ids) ? body.ids : [];
  const answer = ids.map((raw) => ({ id: raw, success: manual.members.includes(raw), error: manual.members.includes(raw) ? null : 'not_found' }));
  manual.members = manual.members.filter((m) => !ids.includes(m));
  save();
  json(ctx.res, 200, answer);
});

// search
const modelNotReady = (ctx) => problem(ctx.res, 503, 'model not ready');

route('GET', /^\/api\/search\/status$/, (ctx) => {
  const total = discoveredAssets().filter((a) => a.isImage).length;
  const embedded = discoveredAssets().filter(hasEmbedding).length;
  json(ctx.res, 200, {
    model: MODEL,
    ready: state.modelReady,
    downloading: download
      ? {
          receivedBytes: Math.min(MODEL_BYTES, Math.round(((Date.now() - download.startedAt) / DOWNLOAD_MS) * MODEL_BYTES)),
          totalBytes: MODEL_BYTES,
          file: 'vision.onnx',
        }
      : null,
    embedded,
    total,
    enabled: state.searchEnabled,
    device: 'cpu',
  });
});

route('POST', /^\/api\/search\/model\/download$/, (ctx) => {
  if (!state.modelReady && !download) download = { startedAt: Date.now() };
  json(ctx.res, 202);
});

route('PUT', /^\/api\/search\/settings$/, async (ctx) => {
  const body = await readBody(ctx.req);
  if (typeof body.enabled === 'boolean') state.searchEnabled = body.enabled;
  save();
  json(ctx.res, 200, { enabled: state.searchEnabled });
});

route('GET', /^\/api\/search\/similar\/([^/]+)$/, (ctx, [, id]) => {
  if (!state.modelReady || !state.searchEnabled) return modelNotReady(ctx);
  const source = byId().get(id);
  if (!source) return problem(ctx.res, 404, 'not found');
  const items = searchFilter(ctx.url, discoveredAssets().filter((a) => a.id !== id && hasEmbedding(a)))
    .map((a) => ({ a, score: scoreFor(source.id, a) + (a.folder === source.folder ? 0.2 : 0) }))
    .sort((x, y) => y.score - x.score)
    .slice(0, limitOf(ctx.url))
    .map(({ a, score }) => ({ ...timelineRow(a), score: Math.round(score * 1000) / 1000 }));
  json(ctx.res, 200, { items, model: MODEL });
});

route('GET', /^\/api\/search\/?$/, (ctx) => {
  const q = (ctx.url.searchParams.get('q') ?? '').trim();
  const metadata = ctx.url.searchParams.get('mode') === 'metadata';
  const limit = limitOf(ctx.url);
  if (metadata) {
    const needle = q.toLowerCase();
    const items = searchFilter(ctx.url, discoveredAssets())
      .filter((a) => !needle || a.name.toLowerCase().includes(needle) || a.relPath.toLowerCase().includes(needle))
      .slice(0, limit)
      .map((a) => ({ ...timelineRow(a), score: 0 }));
    return json(ctx.res, 200, { items, model: 'filename' });
  }
  if (!state.modelReady || !state.searchEnabled) return modelNotReady(ctx);
  const items = searchFilter(ctx.url, discoveredAssets().filter(hasEmbedding))
    .map((a) => ({ a, score: scoreFor(q, a) }))
    .sort((x, y) => y.score - x.score)
    .slice(0, limit)
    .map(({ a, score }) => ({ ...timelineRow(a), score }));
  json(ctx.res, 200, { items, model: MODEL });
});

// library
function rootDto(root) {
  const assets = assetsOf(root).slice(0, root.discovered);
  const trashed = assets.filter((a) => state.trashed.includes(a.id)).length;
  const copies = assets.filter((a) => a.duplicate).length;
  return {
    id: root.id,
    path: root.path,
    label: root.label,
    enabled: root.enabled,
    counts: { files: assets.length + copies, contents: assets.length, trashed },
    lastScan: root.lastScan,
    scanning: root.scanning,
  };
}

function licenceDto() {
  const indexed = discoveredAssets().length;
  return { state: state.licence, cap: CAP, indexed, capReached: !licensed() && indexed > CAP };
}

route('GET', /^\/api\/library$/, (ctx) => {
  const found = discoveredAssets();
  const eligible = eligibleCount();
  const images = found.filter((a) => a.isImage);
  const embeddable = licensed() ? images.length : images.filter((a) => ordinalOf(a) < CAP).length;
  const embedded = found.filter(hasEmbedding).length;
  json(ctx.res, 200, {
    roots: state.roots.map(rootDto),
    thumbnails: { pending: Math.max(0, eligible - thumbsDone), done: thumbsDone },
    embeddings: { pending: Math.max(0, embeddable - embedded), done: embedded, model: MODEL, ready: state.modelReady },
    licence: licenceDto(),
    watch: { running: state.roots.length > 0, lastTick: iso(watchTick) },
    imports: {
      running: [...imports.values()].filter((job) => importDto(job).state === 'running').length,
      ...(lastImportId ? { last: { ...importDto(imports.get(lastImportId)), items: [] } } : {}),
    },
    tesseraFolder: { path: state.tesseraFolder ?? TESSERA_FOLDER_DEFAULT },
  });
});

route('POST', /^\/api\/library\/roots$/, async (ctx) => {
  const body = await readBody(ctx.req);
  const path = typeof body.path === 'string' ? body.path.trim() : '';
  // A path looks like one: a drive or a slash. "not a directory" otherwise.
  if (!/^([a-zA-Z]:[\\/]|\/|\\\\)/.test(path)) return problem(ctx.res, 400, `"${path}" is not a directory`);
  const normal = path.replace(/[\\/]+$/, '').toLowerCase();
  for (const root of state.roots) {
    const existing = root.path.replace(/[\\/]+$/, '').toLowerCase();
    if (normal === existing) return problem(ctx.res, 409, 'That folder is already in the library');
    if (normal.startsWith(`${existing}\\`) || normal.startsWith(`${existing}/`)) {
      return problem(ctx.res, 409, `That folder is inside ${root.path}, which is already in the library`);
    }
  }
  const root = {
    id: state.nextRootId++,
    path,
    label: path.split(/[\\/]/).filter(Boolean).pop() ?? path,
    enabled: true,
    addedAt: new Date().toISOString(),
    discovered: 0,
    scanning: false,
    lastScan: null,
    runs: 0,
  };
  state.roots.push(root);
  save();
  json(ctx.res, 201, rootDto(root));
});

route('DELETE', /^\/api\/library\/roots\/(\d+)$/, (ctx, [, id]) => {
  const index = state.roots.findIndex((r) => r.id === Number(id));
  if (index === -1) return problem(ctx.res, 404, 'not found');
  state.roots.splice(index, 1);
  // The queues shrink with the library; recount what is done.
  thumbsDone = Math.min(thumbsDone, eligibleCount());
  embedsDone = Math.min(embedsDone, eligibleCount());
  save();
  json(ctx.res, 204);
});

route('POST', /^\/api\/library\/roots\/(\d+)\/scan$/, (ctx, [, id]) => {
  const root = state.roots.find((r) => r.id === Number(id));
  if (!root) return problem(ctx.res, 404, 'not found');
  if (root.scanning) return problem(ctx.res, 409, 'A scan of that folder is already running');
  root.scanning = true;
  root.runs += 1;
  root.lastScan = { ...(root.lastScan ?? { seen: 0, added: 0, moved: 0, gone: 0, errors: 0 }), startedAt: new Date().toISOString(), finishedAt: null };
  if (root.runs > 1) root.discovered = 0; // a rescan walks everything again
  save();
  json(ctx.res, 202, { runId: root.runs });
});

route('GET', /^\/api\/library\/duplicates$/, (ctx) => {
  const limit = Math.min(1000, Number(ctx.url.searchParams.get('limit') ?? 200) || 200);
  const sets = discoveredAssets()
    .filter((a) => a.duplicate)
    .slice(0, limit)
    .map((a, i) => ({
      contentId: hashString(a.id) % 1_000_000,
      sha256: a.sha256,
      byteSize: a.byteSize,
      locations: [
        { id: i * 2 + 1, rootId: a.rootId, relPath: a.relPath },
        { id: i * 2 + 2, rootId: a.rootId, relPath: `copies/${a.name}` },
      ],
    }));
  json(ctx.res, 200, sets);
});

// imports (contract §4): a job per drop or pick. The mock cannot copy or
// fetch anything, so a file counts as copied, a folder becomes a root as on
// the Library page, a link is skipped; each job finishes after a second and
// a half, so the progress toast can be seen moving.
const imports = new Map();
let nextImportId = 1;
let lastImportId = null;
const TESSERA_FOLDER_DEFAULT = 'C:/Users/me/Pictures/Tessera';
const NO_IMPORT_COUNTS = { added: 0, already: 0, copied: 0, skipped: 0, folders: 0 };

function importDto(job) {
  const elapsed = Date.now() - job.started;
  const done = job.items.length === 0 ? 0 : Math.min(job.items.length, Math.floor((elapsed / 1500) * job.items.length));
  const items = job.items.map((item, i) => (i < done ? item : { source: item.source, kind: item.kind, outcome: 'pending' }));
  const counts = { ...NO_IMPORT_COUNTS };
  for (const item of items.slice(0, done)) {
    if (item.outcome === 'skipped') counts.skipped += 1;
    else if (item.kind === 'folder') counts.folders += 1;
    else counts[item.outcome] += 1;
  }
  const finished = done === job.items.length;
  return {
    id: job.id,
    ...(job.albumId ? { albumId: job.albumId } : {}),
    state: finished ? 'done' : 'running',
    startedAt: iso(job.started),
    ...(finished ? { finishedAt: iso(job.started + 1500) } : {}),
    total: job.items.length,
    done,
    counts,
    items,
  };
}

function startMockImport(items, albumId) {
  const job = { id: nextImportId++, albumId, started: Date.now(), items };
  imports.set(job.id, job);
  lastImportId = job.id;
  return job;
}

route('POST', /^\/api\/import$/, async (ctx) => {
  const body = await readBody(ctx.req);
  const paths = Array.isArray(body.paths) ? body.paths.filter((p) => typeof p === 'string' && p.trim()) : [];
  const urls = Array.isArray(body.urls) ? body.urls.filter((u) => typeof u === 'string' && u.trim()) : [];
  if (paths.length + urls.length === 0) return problem(ctx.res, 400, 'Nothing to import: give paths or urls');
  if (body.albumId && !allAlbums().some((a) => a.id === body.albumId)) return problem(ctx.res, 404, `No album ${body.albumId}`);
  const items = [
    ...paths.map((path) => {
      const looksLikeFile = /\.[a-z0-9]{2,5}$/i.test(path);
      if (looksLikeFile) return { source: path, kind: 'file', outcome: 'copied', path: `${state.tesseraFolder ?? TESSERA_FOLDER_DEFAULT}/Unsorted/${path.split(/[\\/]/).pop()}` };
      const root = {
        id: state.nextRootId++,
        path,
        label: path.split(/[\\/]/).filter(Boolean).pop() ?? path,
        enabled: true,
        addedAt: new Date().toISOString(),
        discovered: 0,
        scanning: true,
        lastScan: { seen: 0, added: 0, moved: 0, gone: 0, errors: 0, startedAt: new Date().toISOString(), finishedAt: null },
        runs: 1,
      };
      state.roots.push(root);
      return { source: path, kind: 'folder', outcome: 'added', path, rootId: root.id, pictures: ASSETS_PER_ROOT };
    }),
    ...urls.map((url) => ({ source: url, kind: 'url', outcome: 'skipped', reason: 'the mock does not fetch links' })),
  ];
  save();
  json(ctx.res, 202, importDto(startMockImport(items, body.albumId)));
});

route('GET', /^\/api\/import\/(\d+)$/, (ctx, [, id]) => {
  const job = imports.get(Number(id));
  if (!job) return problem(ctx.res, 404, `No import ${id}`);
  json(ctx.res, 200, importDto(job));
});

route('POST', /^\/api\/import\/upload$/, async (ctx) => {
  const name = ctx.url.searchParams.get('name') ?? '';
  if (!name) return problem(ctx.res, 400, 'name is required: the file\'s own name');
  for await (const _chunk of ctx.req) {
    // the bytes are read and dropped: the mock keeps no files
  }
  const job = startMockImport(
    [{ source: name, kind: 'upload', outcome: 'copied', path: `${state.tesseraFolder ?? TESSERA_FOLDER_DEFAULT}/Unsorted/${name}` }],
    ctx.url.searchParams.get('albumId') ?? undefined,
  );
  job.started -= 1500;
  json(ctx.res, 200, importDto(job));
});

// settings and licence
const settingsDto = () => ({
  ...state.settings,
  version: '0.0.0-mock',
  dataDir: 'C:\\Users\\me\\AppData\\Local\\TesseraDesktop (mock)',
  tesseraFolder: state.tesseraFolder ?? TESSERA_FOLDER_DEFAULT,
});
route('GET', /^\/api\/settings$/, (ctx) => json(ctx.res, 200, settingsDto()));
route('PUT', /^\/api\/settings$/, async (ctx) => {
  const body = await readBody(ctx.req);
  if (typeof body.thumbnailConcurrency === 'number') {
    state.settings.thumbnailConcurrency = Math.min(16, Math.max(1, Math.round(body.thumbnailConcurrency)));
  }
  if (typeof body.embedOnBattery === 'boolean') state.settings.embedOnBattery = body.embedOnBattery;
  if (typeof body.tesseraFolder === 'string') {
    if (!/^([a-zA-Z]:[\\/]|\/)/.test(body.tesseraFolder)) return problem(ctx.res, 400, 'The Tessera folder must be an absolute path.');
    state.tesseraFolder = body.tesseraFolder.replace(/\\/g, '/');
  }
  save();
  json(ctx.res, 200, settingsDto());
});

route('GET', /^\/api\/licence$/, (ctx) => json(ctx.res, 200, licenceDto()));
route('PUT', /^\/api\/licence$/, async (ctx) => {
  const body = await readBody(ctx.req);
  if (body.state === 'free' || body.state === 'licensed') state.licence = body.state;
  save();
  json(ctx.res, 200, licenceDto());
});

// ------------------------------------------------------------------ server

const server = createServer(async (req, res) => {
  const started = Date.now();
  const url = new URL(req.url ?? '/', `http://127.0.0.1:${PORT}`);
  cors(req, res);
  res.on('finish', () => {
    if (!url.pathname.endsWith('/thumbnail')) {
      process.stderr.write(`${req.method} ${url.pathname} → ${res.statusCode} (${Date.now() - started} ms)\n`);
    }
  });

  if (req.method === 'OPTIONS') {
    res.writeHead(204);
    res.end();
    return;
  }

  if (url.pathname.startsWith('/api') && !authorised(req, url)) {
    return problem(res, 401, 'missing or invalid token');
  }

  for (const { method, pattern, handler } of routes) {
    if (method !== req.method) continue;
    const match = pattern.exec(url.pathname);
    if (!match) continue;
    try {
      await handler({ req, res, url }, match);
    } catch (error) {
      process.stderr.write(`! ${req.method} ${url.pathname}: ${error?.stack ?? error}\n`);
      if (!res.headersSent) problem(res, 500, String(error?.message ?? error));
    }
    return;
  }

  problem(res, 404, `no route for ${req.method} ${url.pathname}`);
});

server.listen(PORT, '127.0.0.1', () => {
  // The same one JSON line the real core prints (contract §1).
  process.stdout.write(`${JSON.stringify({ event: 'ready', port: PORT, pid: process.pid, version: '0.0.0-mock' })}\n`);
  process.stderr.write(
    `mock core on http://127.0.0.1:${PORT} · token ${TOKEN} · cap ${CAP} · ${state.roots.length} root(s) from ${existsSync(STATE_FILE) ? '.state.json' : 'scratch'}\n`,
  );
});

// The real core shuts down when stdin reaches EOF (contract §1). Opt in to
// the same here with MOCK_STDIN_EOF=1; off by default, because a dev server
// started from a script or a task runner often has no stdin at all and would
// exit before answering a request.
if (process.env.MOCK_STDIN_EOF === '1') {
  process.stdin.resume();
  process.stdin.on('end', () => process.exit(0));
}
