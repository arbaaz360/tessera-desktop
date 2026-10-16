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
| `src/Tessera.Core` | Pure rules: hashing, path spelling, scan safety, capture-date parsing, the domain records |
| `src/Tessera.Data` | SQLite: the schema (embedded migrations), Dapper repositories, the work queue |
| `src/Tessera.Api` | Kestrel, the boot handshake, token auth, the routes, the workers, the header reader, the fake search |
| `tests/Tessera.Tests` | 173 tests: repositories on a temp database, the scanner over real PNGs and JPEGs, header bytes written by hand (EXIF, eXIf, XMP, MP4), a 200,000-row timeline, the boot handshake against the real exe |
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
`/api/albums/overlap` (an empty list: one account). `PATCH /api/albums/{id}`
also takes `albumThumbnailAssetId`, the cover choice, as Immich's does.
`/api/download/archive`,
`/api/media/{id}/thumb|original`, `/api/scan/roots/{id}/scan?folder=`,
`/api/scan/folders`.

New (CONTRACT §4): `/api/library` and its `roots` add, remove and scan,
`/api/library/duplicates`; `/api/search`, `/api/search/similar/{id}`,
`/api/search/status`, `/api/search/model/download`, `/api/search/settings`;
`/api/settings`; `/api/licence`. Status answers carry counts: `thumbnails`
and `embeddings` report `pending`, `done` and `failed`; a root reports its
files, contents, trashed, and its last scan's seen/added/moved/copies/gone/errors,
refreshed every two seconds while it runs.

Two additive fields in `GET /api/library`, beyond the contract:

| Field | Meaning |
|---|---|
| `roots[].lastScan.copies` | known bytes found at a new path while another live copy is still on disk. `moved` is only real moves now: the old path is gone |
| `dates` | `{"pending", "done", "exif", "filename", "mtime", "unreadable"}`: contents with a live location by where their date came from. `pending` (not read yet by the backfill) + `exif` + `filename` + `mtime` = all of them; `done` is the last three. `unreadable` is how many the latest backfill pass could not open (drive not mounted, file changed since the scan); they stay pending. Counted at most every 10 s |

## How it works, briefly

| Piece | Rule |
|---|---|
| Identity | a file is its sha256; the path is an attribute on `location`. A copy is a second location, a deletion is `gone_at`, a move is a new location with the old one retired at once |
| Copy or move | known bytes at a new path are a **copy** while another live location of them is still on disk (same size), and a **move** when none is. A walk cannot tell from what it has seen, since the old path may not have been reached yet, so the scanner stats the other locations, inside the single writer so two threads meeting a pair cannot both be first. A move retires the old row then and there and carries its trash mark, so a rename is `moved 1, gone 0`, and a move seen by a scan of the destination folder alone is still a move. A root that is not mounted counts as still there (copy). Two identical files in a fresh folder are `added 1, copies 1` |
| Capture dates | EXIF DateTimeOriginal, then DateTimeDigitized, then DateTime, each with its own offset tag (OffsetTimeOriginal …) and sub-seconds; then XMP (`exif:DateTimeOriginal`, `photoshop:DateCreated`, `xmp:CreateDate`), which is where PNG, WebP and edited files keep theirs; then the QuickTime/MP4 creation time (Apple's `creationdate` with its offset first, then `mvhd`, UTC). All of these are `taken_src = 'exif'`. Else a date in the file name (`filename`), else the modified time (`mtime`). A later sighting only replaces a date with a strictly better source. Refused, and the next source tried: before 1900, more than a day ahead, or not a calendar date |
| File-name dates | `IMG_/VID_/PXL_20240101_123456[789]`, `20240101_123456`, `IMG20240101123456`, `2024-01-01 12.34.56` (and `_`, `-`), `Screenshot_20240101-123456`, `Screenshot_2024-01-01-12-34-56`, `Screenshot 2024-01-01 at 1.23.45 PM` (macOS, with its narrow space), `Screenshot 2024-01-01 123456` (Windows), `signal-…`, `photo_…` (Telegram), and the day alone from `IMG-20240101-WA0001` (WhatsApp) and `2019-07-04 …`, read as noon. The name only, never the folder. The first pattern that matches decides: an impossible date there (30 February, hour 25, 13 PM) gives no date rather than a looser match further down |
| Headers only | MetadataExtractor walks JPEG segments to the start of scan, PNG and RIFF chunks, ISO-BMFF boxes, and **seeks** past what it does not want; a 16 MB `mdat` in front of a camera's `moov` is skipped, not read. A 4 MB read budget per file stops a malformed one. Width and height come from the same read, turned by EXIF orientation 5–8 (or HEIF `irot`, QuickTime track rotation), and so does a video's duration. A file that will not parse says nothing and falls back; it is never a scan error |
| Header backfill | `content.meta_version` records which reader looked at a content (0 = none). The scanner reads headers when it first hashes a content; `HeaderBackfill` reads the rest (everything indexed before this) in content-id order, 200 per batch under the disk lock, one transaction per batch. Resumable without a cursor: what is left is a query on a partial index that is empty once it is done. The trigger on `content.taken_at` retimes `timeline_entry`; no code touches the timeline |
| Thumbnails turned | the WebP is turned by the original's EXIF orientation (on the 320 px result, not the original), so the grid shows a phone's portrait shot upright, the same shape the headers recorded |
| Timeline | `timeline_entry` is kept by triggers equal to "has a live, untrashed location"; paged by keyset `(taken_at, content_id)` on an index in that order |
| Scans | reconciliation, not events: walk, hash what changed (size+mtime trusted), mark what was not seen. A walk that finds under half of what is known marks nothing gone. One scan at a time, queued per root or folder; the run row exists from the moment it is queued |
| Watch | one stat per root per minute on the parent folder, a few hundred child stats round-robin; a folder that stops changing for a minute is queued for a scan. Nothing waits for a person |
| Thumbnails | 320 px WebP at `thumbs/ab/cd/<sha>-thumb.webp`, path derived never stored. Skia; `sips` on macOS for HEIC and friends; elsewhere those are failed items with the reason |
| Embeddings | decode up to 8 thumbnails, one `EmbedImages` call for the batch (the real model batches onto the GPU: 116 against 17 images/s one at a time), `IVectorIndex.Upsert` each, the `embedding` ledger rows in one transaction; the index file `vectors/<model>.bin` is saved every minute while it changes and at shutdown, and reconciled with the ledger at start |
| Search | words to a vector, cosine over the index, then album, kind and date filters against SQLite. `mode=metadata` is a file-name query and never needs the model. "More like this" reads the picture's vector back with `TryGet` and only re-embeds the thumbnail when the index does not hold it |
| CORS | the shell's origin (plus Vite's under `TESSERA_DEV=1`): GET, POST, PUT, PATCH, DELETE, OPTIONS; headers Authorization, Content-Type, Range; credentials; preflight answers 204 |
| Free tier | while `free` and more than 2,000 distinct contents are indexed, the first 2,000 by id keep working and the rest get no thumbnail or vector; `PUT /api/licence {"state":"licensed"}` queues them |
| Writes | one in-process writer (`Db.OpenWriteAsync`). Microsoft.Data.Sqlite waits for a busy database by sleeping 150 ms, so letting four threads contend cost a second per write; queued in-process, a rescan of 200 files went from 5 s to 23 ms |
| Files | image files are opened through .NET streams, never Skia's `fopen`, which is ANSI and bound by MAX_PATH; a 259-character thumbnail path once failed every embedding |

### A capture time with no offset

Most cameras before about 2018, and many since, write DateTimeOriginal with
no offset: a wall-clock reading in a zone nobody recorded. **The core reads
it in the zone of the machine doing the scan, with that zone's rule for that
date** (a July photo in London is BST, a January one GMT), and stores the UTC
instant that results. File-name dates follow the same rule.

Why: the library is one person's, on their own machine, and most of their
pictures were taken where they live. Read there, these instants sort
correctly against everything that is a true instant: photos with an offset,
MP4 creation times (UTC), file times. And the web app, which shows instants
in the viewer's zone, shows the clock face the camera recorded. Storing the
digits as if they were UTC would show that clock face only to a viewer in
UTC, and sort those pictures up to fourteen hours away from their
neighbours.

What it costs: a camera left on home time while travelling shows home time,
as it does on the camera; and if the machine moves to another zone, these
pictures shift by the difference in display, as every other instant does.
An hour that happens twice is read as standard time; an hour that never
happened gets the standard offset, landing just after the jump. A
GPS-derived zone would do better for travel photos; it is not done.

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
  `InMemoryVectorIndex` stand in until the search library is wired in; the
  swap is the two `AddSingleton` lines under "search" in `Program.cs`. Text
  search therefore returns something but means nothing yet. The core does
  not reference ONNX Runtime; the search library picks DirectML or the base
  package by RID.
- **Video thumbnails.** No ffmpeg in this build: videos are indexed, listed
  and served, but have no thumbnail and are not counted as pending.
- **HEIC, AVIF, TIFF, JXL, RAW on Windows.** Recorded as failed items with
  the reason. The macOS `sips` path is written but has not run on a Mac here,
  and neither has the `osx-arm64` publish.
- **Capture dates from real cameras.** The header tests use bytes written
  by hand: EXIF with and without offsets, eXIf and XMP in PNG, an MP4 movie
  header. No HEIC, WebP-with-EXIF, RAW, AVI or maker-note-heavy camera JPEG
  has been through the reader here; MetadataExtractor handles them, and a
  failure to parse falls back to the name and the file time rather than
  erroring.
- **Thumbnails made before the orientation fix** stay as they were (sideways
  for a turned photo) until regenerated; their recorded shape matches them,
  because the backfill only fills a missing width and height.
- **The zone of a floating time is the machine's**, not the place the photo
  was taken; GPS is not read.
- **Battery detection** is Windows `GetSystemPowerStatus` and macOS `pmset`;
  the macOS half is untested.
- The watch polls directory mtimes; there is no OS file-system watcher.
- `GET /api/scan/folders` lost `autoScanMaxEntries`; nothing waits for a
  person any more. `waitingForYou` is kept and always false.
- `IImageEmbedder.EmbedImages` and `IVectorIndex.TryGet` are on the fakes
  and called as if on the interfaces; until the coordinator's interface
  change lands, `Search/InterfaceShims.cs` routes the calls. Delete it then.
