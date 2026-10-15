# Integration contract

Four parts, four owners, one contract. **A part changes only its own folder.
This file is frozen for the parts.** If a part needs it changed, it tells the
coordinating session (the one that started it) and keeps working on what does
not depend on the change.

| Part | Folder | Produces |
|---|---|---|
| core | `core/` | `tessera-core`: a .NET 10 self-contained console app (Kestrel) over SQLite |
| search | `search/`, `models/` | `Tessera.Desktop.Search`: SigLIP2 through ONNX Runtime, a vector index, the model export scripts |
| web | `web/` | the React app, built to `web/dist` |
| shell | `src-tauri/`, `.github/` | the Tauri 2 app that bundles `web/dist` and the core's publish output, and CI |

Written 2026-10-16 by the coordinator. The product plan is `docs/PLAN.md`.

---

## 1. Boot handshake: shell ↔ core

The shell starts the core executable with these environment variables.

| Variable | Required | Meaning |
|---|---|---|
| `TESSERA_TOKEN` | yes | 32 random bytes as base64url (43 characters). Every `/api` request must carry it (§2) |
| `TESSERA_DATA_DIR` | yes | absolute path. The core creates it and everything under it (§7) |
| `TESSERA_WEB_ORIGIN` | yes | the one origin CORS allows: `tauri://localhost` on macOS, `http://tauri.localhost` on Windows |
| `TESSERA_PORT` | no | unset or `0`: the core picks a free loopback port |
| `TESSERA_DEV` | no | `1` also allows the origin `http://localhost:5173` (Vite dev) |

Rules:

- The core listens on **127.0.0.1 only**. Never `0.0.0.0` in v1.
- When it can serve requests, the core prints **one JSON line to stdout**:
  `{"event":"ready","port":N,"pid":P,"version":"1.0.0"}` and nothing else on
  stdout, ever. Logs go to stderr (one JSON object per line) and to
  `logs/core-<date>.log`.
- The core writes `core.pid` in the data dir. If it finds one naming a live
  process it exits with code 3.
- **The core keeps stdin open and shuts down when stdin reaches EOF.** The
  shell holds the pipe; a shell that dies closes it; no orphans.
- The shell kills the core on exit, and on start kills any stale pid named in
  `core.pid` before spawning.
- `GET /health/live` → 200 as soon as the process is up; `GET /health/ready`
  → 200 once the database is migrated and workers are running. No token.

## 2. Authentication: web ↔ core

Every request under `/api` carries the token, one of:

- header `Authorization: Bearer <token>`
- query `sessionKey=<token>` — for `<img>`, `<video>` and downloads, which
  cannot send headers. `web/src/shared/api/client.ts` already appends
  `sessionKey` to media URLs.

Missing or wrong → `401 {"detail":"…"}`. Accounts, login, cookies and API
keys are gone. One built-in account (`account_id = 1`) exists in the
database and every query is scoped to it, so repository code keeps its
`accountId` parameters.

## 3. Routes the web app already calls: unchanged

The calls the web app makes today keep their **path, verb, query parameter
names and JSON shapes**. Neither side renames one. They are, from
`web/src` at the fork commit:

| Kept | Used by |
|---|---|
| `/api/timeline/buckets`, `/api/timeline/bucket`, `/api/timeline` | grid |
| `/api/assets/{id}`, `/api/assets/{id}/thumbnail`, `/api/assets/{id}/original`, favourites and trash operations on assets | viewer, selection, trash |
| `/api/albums`, `/api/albums/{id}`, `/api/albums/{id}/assets`, `/api/albums/overlap` | albums |
| `/api/search` with the query parameter names in `web/src/features/search/api.ts` | search — the core now answers it locally (§4) |
| `/api/scan/roots/{id}/scan`, `/api/scan/folders` | Library page |

Dropped — the core answers 404 and the web app stops calling them:
`/api/auth/*`, `/api/users*`, `/api/server/config`, `/api/admin/*`,
`/api/faces/*`, `/api/music/*`, `/api/jobs/*`, `/api/assets/{id}/identify`,
`/api/assets/{id}/music`, `/api/albums/{id}/index`, `/api/queues`,
`/api/workers`, covers.

## 4. New routes: core implements, web calls

All JSON. Errors are an HTTP status plus `{"detail": "…"}`. Times are ISO
8601 UTC strings. Status answers carry **numbers, never a green tick**.

### Library

`GET /api/library`

```json
{
  "roots": [{
    "id": 1, "path": "C:\\Users\\me\\References", "label": "References", "enabled": true,
    "counts": {"files": 12034, "contents": 11870, "trashed": 3},
    "lastScan": {"startedAt": "…", "finishedAt": "…", "seen": 12034, "added": 10, "moved": 2, "gone": 0, "errors": 0},
    "scanning": false
  }],
  "thumbnails": {"pending": 120, "done": 11750},
  "embeddings": {"pending": 300, "done": 11570, "model": "siglip2-base-patch16-256", "ready": true},
  "licence": {"state": "free", "cap": 2000, "indexed": 11870, "capReached": true},
  "watch": {"running": true, "lastTick": "…"}
}
```

| Route | Answer |
|---|---|
| `POST /api/library/roots` `{"path": "…"}` | `201` the root as above; `400` not a directory; `409` already a root or inside one |
| `DELETE /api/library/roots/{id}` | `204`. Removes the root's `location` rows. Files are untouched. `content` rows left without a location stay (albums keep their members) |
| `POST /api/library/roots/{id}/scan` | `202 {"runId": N}`; `409` if a scan of that root is running |
| `GET /api/library/duplicates?limit=200` | `[{"contentId": N, "sha256": "…", "byteSize": N, "locations": [{"id": N, "rootId": N, "relPath": "…"}]}]` — contents with two or more live locations |

### Search

| Route | Answer |
|---|---|
| `GET /api/search?…` | the query parameter names the web app uses today, plus `limit` (default 100, max 500). Answer: `{"items": [TimelineItem + "score": 0.31], "model": "…"}` where `TimelineItem` is exactly the asset shape `/api/timeline/bucket` returns, so the grid renders it unchanged. `503 {"detail":"model not ready"}` until the model is loaded |
| `GET /api/search/similar/{contentId}?limit=100` | same shape, the image's own vector as the query |
| `GET /api/search/status` | `{"model": "…", "ready": false, "downloading": {"receivedBytes": N, "totalBytes": N, "file": "vision.onnx"} or null, "embedded": N, "total": N, "enabled": true, "device": "cpu" or "directml" or "coreml"}` |
| `POST /api/search/model/download` | `202`; idempotent |
| `PUT /api/search/settings` `{"enabled": true}` | `200` |

### Settings and licence

| Route | Answer |
|---|---|
| `GET /api/settings` | `{"thumbnailConcurrency": 4, "embedOnBattery": false, "version": "1.0.0", "dataDir": "…"}` |
| `PUT /api/settings` (same shape, partial) | `200` the full settings |
| `GET /api/licence` | `{"state": "free" or "licensed", "cap": 2000, "indexed": N, "capReached": false}` |
| `PUT /api/licence` `{"state": "licensed"}` | `200`. The **shell** calls this after verifying with Gumroad; the core trusts it (loopback, token) and persists it |

**Free tier rule (core enforces):** while `state == "free"` and the count of
distinct `content` rows across live locations exceeds `cap`, the scanner
still records new files, but **no thumbnail and no embedding is produced
for content beyond the cap**. `GET /api/library` reports `capReached`. The
web app shows the unlock prompt on that flag. Setting the state to
`licensed` releases the queue.

## 5. Shell commands: web ↔ shell (Tauri `invoke`)

| Command | Answer |
|---|---|
| `core_info()` | `{"baseUrl": "http://127.0.0.1:N", "token": "…"}` |
| `pick_folder()` | an absolute path, or `null` |
| `licence_get()` | `{"state": "free" or "licensed", "keyMasked": "XXXX-…-1234" or null, "checkedAt": "…" or null}` |
| `licence_activate(key)` | `{"state": "…", "error": null or "invalid" or "offline"}` — verifies with Gumroad, stores the key in the OS keychain, then `PUT /api/licence` on the core |
| `open_external(url)` | opens the system browser |
| `app_version()` | `"1.0.0"` |

The web app detects Tauri by `window.__TAURI_INTERNALS__`. Outside Tauri
(Vite dev, tests) it reads `VITE_CORE_URL` and `VITE_CORE_TOKEN` from
`.env.local` and uses the dialog-free fallbacks (a text field for the path).

## 6. Search library: core ↔ search

`search/src/Tessera.Desktop.Search.Abstractions/` holds the interfaces.
**Written by the coordinator, frozen.** The search part implements them in
`Tessera.Desktop.Search` and exposes
`IServiceCollection.AddTesseraSearch(SearchOptions)`. The core consumes
`IImageEmbedder` and `IVectorIndex` through DI and, until the real library is
merged, registers its own `FakeImageEmbedder` (deterministic vectors derived
from the sha256) and a trivial in-memory index so everything runs end to end.

Model files live under `<data>/models/<modelId>/`: `vision.onnx`,
`text.onnx`, `tokenizer.model`, `manifest.json`. The manifest (shipped inside
the library as an embedded resource and also published at the download URL)
lists each file's URL, size and sha256. The index persists to
`<data>/vectors/<modelId>.bin`.

## 7. Data dir layout

```
<TESSERA_DATA_DIR>/
  library.db              SQLite, WAL mode
  thumbs/ab/cd/<sha>-thumb.webp
  models/<modelId>/       vision.onnx  text.onnx  tokenizer.model  manifest.json
  vectors/<modelId>.bin
  logs/core-YYYY-MM-DD.log
  core.pid
```

Default data dir (the shell decides): `%LOCALAPPDATA%\TesseraDesktop` on
Windows, `~/Library/Application Support/TesseraDesktop` on macOS.

## 8. Packaging

| Part | Command | Output |
|---|---|---|
| core | `dotnet publish core/src/Tessera.Api -c Release -r <rid> --self-contained -o src-tauri/resources/core` for `win-x64` and `osx-arm64`. **Not single-file.** `AssemblyName` is `tessera-core` | `src-tauri/resources/core/tessera-core(.exe)` plus its native libraries |
| web | `pnpm --dir web build` | `web/dist` |
| shell | `tauri.conf.json`: `frontendDist: "../web/dist"`, `devUrl: "http://localhost:5173"`, `bundle.resources: ["resources/core/**"]`; the shell runs the core from its resource dir | NSIS and MSI on Windows, DMG on macOS arm64 |

Signing and notarization: placeholders in CI; unsigned builds until the
certificates exist.

## 9. Product rules every part obeys

- **Never write inside a library root. Never delete a file.** Trash is
  `trashed_at`; `POST /api/trash/empty` answers 403.
- **No network** except: model download (the URLs in the manifest), Gumroad
  licence verification (shell), the update feed (shell).
- **Numbers, not states.** Every status answer reports counts.
- One fact, one place: a thumbnail's path is derived from the hash, never
  stored; a file's identity is its sha256, never its path.
