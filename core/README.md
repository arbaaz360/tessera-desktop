# tessera-core

The engine of Tessera Desktop: a .NET 10 console app that the shell spawns,
listening on `127.0.0.1` with a per-launch token, over one SQLite file. It
scans the folders the buyer chose (read-only), hashes every file, keeps the
timeline, albums, favourites and trash, makes thumbnails, turns them into
vectors, and answers the web app's `/api`. The agreement with the other parts
is `docs/CONTRACT.md`; this file says how the core meets it and what it
measures.

| Folder | What |
|---|---|
| `src/Tessera.Core` | Pure rules: hashing, path spelling, scan safety, the domain records |
| `src/Tessera.Data` | SQLite: the schema (one embedded migration), Dapper repositories, the work queue |
| `src/Tessera.Api` | Kestrel, the boot handshake, token auth, the routes, the workers, the fake search |
| `tests/Tessera.Tests` | 93 tests: repositories on a temp database, the scanner over real PNGs, a 200,000-row timeline, the boot handshake against the real exe |
| `tessera-core.slnx` | So that `dotnet test core` works from the repo root |

## Build, test, publish

```bash
dotnet test core
dotnet publish core/src/Tessera.Api -c Release -r win-x64 --self-contained -o src-tauri/resources/core
```

The publish output is 351 files and 121 MB (the .NET runtime, Skia,
e_sqlite3). `tessera-core.exe` is the apphost and needs the folder beside it.
The boot tests use that published exe when it exists, otherwise the one the
build put next to the test assembly.

## Run it by hand

The shell sets five variables and holds the stdin pipe. By hand, set them
yourself and keep stdin open (an interactive terminal does; a script must
pipe something that stays alive).

```powershell
$env:TESSERA_TOKEN = "dev-token-0123456789abcdefghijklmnop"
$env:TESSERA_DATA_DIR = "$env:LOCALAPPDATA\TesseraDesktop-dev"
$env:TESSERA_WEB_ORIGIN = "http://tauri.localhost"
$env:TESSERA_DEV = "1"                 # also allows http://localhost:5173
.\src-tauri\resources\core\tessera-core.exe
```

| Variable | Meaning |
|---|---|
| `TESSERA_TOKEN` | required, at least 16 characters; every `/api` call carries it |
| `TESSERA_DATA_DIR` | required, absolute; created, with `thumbs/ models/ vectors/ logs/` under it |
| `TESSERA_WEB_ORIGIN` | required; the one origin CORS allows |
| `TESSERA_PORT` | unset or `0`: a free port is chosen |
| `TESSERA_DEV` | `1` also allows the Vite origin |

What it prints: exactly one line on stdout, `{"event":"ready","port":N,"pid":P,"version":"1.0.0"}`.
Everything else is JSON lines on stderr and in `logs/core-YYYY-MM-DD.log`.
It writes `core.pid`; a second core on the same data dir exits with code 3,
a missing variable with code 2. Close stdin (or Ctrl+C) and it stops; the
pid file goes with it.

Then, with the port from the ready line:

```bash
B=http://127.0.0.1:PORT; T=$TESSERA_TOKEN
curl -H "Authorization: Bearer $T" $B/api/library
curl -H "Authorization: Bearer $T" -H "Content-Type: application/json" \
     -d '{"path":"C:\\Users\\me\\Pictures"}' $B/api/library/roots       # 201, and the scan starts
curl -H "Authorization: Bearer $T" $B/api/timeline/buckets
curl -H "Authorization: Bearer $T" "$B/api/timeline/bucket?timeBucket=2024-06-01"
curl "$B/api/assets/1/thumbnail?sessionKey=$T" -o thumb.webp              # the <img> spelling of the token
curl -H "Authorization: Bearer $T" "$B/api/search?q=red%20saree&limit=20"
curl -H "Authorization: Bearer $T" $B/api/search/status
curl -X PUT -H "Authorization: Bearer $T" -H "Content-Type: application/json" \
     -d '{"state":"licensed"}' $B/api/licence
curl -H "Authorization: Bearer $T" $B/api/library/duplicates
curl $B/health/ready                                                       # no token
```

Errors are the status plus `{"detail":"…"}`. An unknown `/api` route is a
JSON 404. Ids on the wire are decimal strings (`"123"`); the web app treats
them as opaque.

## Routes

Kept exactly as the web app calls them (CONTRACT §3): `/api/timeline/buckets`,
`/api/timeline/bucket`, `/api/timeline` (keyset page), `/api/assets/{id}`,
`/thumbnail`, `/original`, `/video/playback`, `PUT /api/assets` (favourite),
`DELETE /api/assets` (trash; `force` is a 403), `/api/trash/restore`,
`/api/trash/restore/assets`, `/api/trash/empty` (403, always), `/api/albums`
and `/api/albums/{id}` CRUD, `/{id}/assets`, `/{id}/stats`, `/{id}/cover`,
`/api/albums/overlap` (an empty list: one account), `/api/download/archive`,
`/api/media/{id}/thumb|original`, `/api/scan/roots/{id}/scan?folder=`,
`/api/scan/folders`.

New (CONTRACT §4): `/api/library` and its `roots` add, remove and scan,
`/api/library/duplicates`; `/api/search`, `/api/search/similar/{id}`,
`/api/search/status`, `/api/search/model/download`, `/api/search/settings`;
`/api/settings`; `/api/licence`. Status answers carry counts: `thumbnails`
and `embeddings` report `pending`, `done` and `failed`; a root reports its
files, contents, trashed, and its last scan's seen/added/moved/gone/errors,
refreshed every two seconds while it runs.

## How it works, briefly

| Piece | Rule |
|---|---|
| Identity | a file is its sha256; the path is an attribute on `location`. A move is one UPDATE, a copy is a second location, a deletion is `gone_at` |
| Timeline | `timeline_entry` is kept by triggers equal to "has a live, untrashed location"; paged by keyset `(taken_at, content_id)` on an index in that order |
| Scans | reconciliation, not events: walk, hash what changed (size+mtime trusted), mark what was not seen. A walk that finds under half of what is known marks nothing gone. One scan at a time, queued per root or folder; the run row exists from the moment it is queued |
| Watch | one stat per root per minute on the parent folder, a few hundred child stats round-robin; a folder that stops changing for a minute is queued for a scan. Nothing waits for a person |
| Thumbnails | 320 px WebP at `thumbs/ab/cd/<sha>-thumb.webp`, path derived never stored. Skia; `sips` on macOS for HEIC and friends; elsewhere those are failed items with the reason |
| Embeddings | decode the thumbnail, `IImageEmbedder.EmbedImage`, `IVectorIndex.Upsert`, an `embedding` ledger row; the index file `vectors/<model>.bin` is saved every minute while it changes and at shutdown, and reconciled with the ledger at start |
| Free tier | while `free` and more than 2,000 distinct contents are indexed, the first 2,000 by id keep working and the rest get no thumbnail or vector; `PUT /api/licence {"state":"licensed"}` queues them |
| Writes | one in-process writer (`Db.OpenWriteAsync`). Microsoft.Data.Sqlite waits for a busy database by sleeping 150 ms, so letting four threads contend cost a second per write; queued in-process, a rescan of 200 files went from 5 s to 23 ms |
| Files | image files are opened through .NET streams, never Skia's `fopen`, which is ANSI and bound by MAX_PATH; a 259-character thumbnail path once failed every embedding |

## Numbers

Measured on this machine (Windows 11, NVMe, the published win-x64 build),
over a generated library of 1,000 JPEGs at 1600×1200 (105 MB), with the fake
embedder.

| What | Number |
|---|---|
| Start to ready line | under 1 s |
| Scan, 1,000 new files | 1.86 s, 537 files/s (hash-bound, about 56 MB/s) |
| Rescan, nothing changed | 251 ms, 3,974 files/s (200 files: 23 ms) |
| Thumbnails, 1600×1200 JPEG to 320 px WebP | 155 per second, 4 in parallel; 7.8 KB each |
| Embeddings (fake, one thread) | 473 per second; the real model will set this |
| Timeline page of 200 over 200,000 rows | 0.39 ms at the top, 0.50 ms at 50 %, 0.21 ms at the end |
| `GET /api/timeline/bucket`, 1,000 items | 4.1 ms |
| `GET /api/timeline/buckets`, `/api/library`, a thumbnail, an asset | 2.1, 2.7, 1.9, 1.6 ms |
| `GET /api/search` (top 100 of 1,000), `/similar` | 3.2 ms, 3.9 ms |
| RAM (working set) | 56 MB idle, 110 MB after the scan, 159 MB peak during thumbnails, 144 MB after |
| `library.db` for 1,000 contents | 828 KB |
| Seeding 200,000 contents + timeline rows | 15 s |

## Not done

- **The real model.** `FakeImageEmbedder` (sha256-derived vectors) and
  `InMemoryVectorIndex` stand in until the search library merges; the swap
  is the two `AddSingleton` lines under "search" in `Program.cs`. Text
  search therefore returns something but means nothing yet.
- **Video thumbnails.** No ffmpeg in this build: videos are indexed, listed
  and served, but have no thumbnail and are not counted as pending.
- **HEIC, AVIF, TIFF, JXL, RAW on Windows.** Recorded as failed items with
  the reason. The macOS `sips` path is written but has not run on a Mac here,
  and neither has the `osx-arm64` publish.
- **EXIF.** `taken_at` is the file's mtime (`taken_src = 'mtime'`); no
  EXIF date yet. Dimensions are known once a thumbnail exists (ratio 1 before).
- **Battery detection** is Windows `GetSystemPowerStatus` and macOS `pmset`;
  the macOS half is untested.
- The watch polls directory mtimes; there is no OS file-system watcher.
- `GET /api/scan/folders` lost `autoScanMaxEntries`; nothing waits for a
  person any more. `waitingForYou` is kept and always false.
- "More like this" re-embeds the picture's thumbnail because `IVectorIndex`
  has no way to read a stored vector back; a `TryGet` on the interface
  would save that.
