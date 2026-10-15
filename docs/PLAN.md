> Copied 2026-10-16 from the IGChromeExtension repo (`docs/TESSERA_DESKTOP_V1_PLAN.md`). **The schedule in §7 is superseded**: the owner decided the same day to build fast, in parallel sessions, not over weeks. See `CONTRACT.md` for the parts and their boundaries.

# Tessera Desktop — v1 build plan

Working name **Tessera Desktop** (the product name is open, Q-01). Written
2026-10-16 from the assessment on this branch. It scopes a sellable desktop
version of the HomeLab photo library (`HomeLab/Tessera`, `HomeLab/tessera-web`).

> **In three lines.** A Windows and macOS app that indexes the buyer's own
> image folders in place, never writes to them, finds pictures by describing
> them (SigLIP2 on the buyer's machine, offline), shows duplicates, and keeps
> albums that survive moving files. Sold as a one-time Gumroad licence to
> artists and designers who keep reference libraries. Built as a Tauri 2 shell
> around the existing React app and a .NET sidecar on SQLite.

---

## 1. What v1 is, and is not

### The promise — the five lines on the product page

1. **Your folders stay yours.** Indexed in place, opened read-only. Nothing is
   copied, renamed or deleted. (Eagle copies every file into its own library;
   this does not.)
2. **Find it by describing it.** "woman in a red saree on a beach", "ink
   sketch of a horse". Offline, on your machine.
3. **A file is its bytes.** Move or rename anything and its albums and search
   survive. Identical copies show as one picture with N locations.
4. **Folders are albums.** Each folder under a chosen root is an album and
   stays in sync.
5. **Fast at scale.** 200,000 images scroll without stutter.

### In v1

| Area | Scope |
|---|---|
| Library | pick folders; scan; watch for changes; SHA-256 identity; duplicates view; trash is a mark, never a delete |
| Browse | timeline grid by month, justified layout; viewer with the original; favourites; albums from folders plus manual albums |
| Search | text → images (SigLIP2); "more like this"; filters: album, image/video, date; file-name mode |
| Files | JPEG, PNG, WebP, GIF, BMP, TIFF everywhere; HEIC on macOS; video thumbnails if the ffmpeg sidecar lands in time (§5) |
| App | onboarding; Library page (folders, progress, counts); settings; licence and free tier; auto-update; log export |

### Out of v1, and why

| Not in v1 | Why | When |
|---|---|---|
| Faces, people, "who is this" | InsightFace's `buffalo_l` is non-commercial research only; server or corpus face identity is a legal and reputational hazard; it attracts the segment that never pays | v2 at the earliest, on-device, "people in my library" only, with a commercially licensed model |
| Cloud, SaaS, sync | weakest option in the assessment; biometric and GPU cost on the server | not planned |
| Phone apps | needs a relay to be worth anything | v1.1: the core serves the web app on the LAN as a PWA |
| Editing, RAW, OCR, music, covers, multi-account, Immich compatibility | not the product; each is a HomeLab feature with its own worker | not planned |
| HEIC on Windows | needs a codec we do not ship | v1.x if asked for |
| Libraries over ~300k images | vectors in memory (§4) | usearch on disk when a beta tester hits it |

### Who it is for

Keepers of reference libraries: both paying MMD customers are artists who
download whole profiles in their genre. Illustrators, calligraphers,
designers, photographers with folder archives. **Not** self-hosters (Immich is
free) and not families wanting cloud backup (Google and Apple).

---

## 2. Architecture

```
┌──────────────────── Tauri 2 app (Rust, kept small) ─────────────────────┐
│  window: the React app (tauri://localhost, fork of tessera-web)          │
│  plugins: shell (spawn the core), dialog (pick folders), updater,        │
│           single-instance, keychain (licence key),                       │
│           http allow-list: Gumroad licence API + update feed, nothing else│
│                        │ spawn: port + 32-byte token (env, not argv)      │
│                        ▼                                                 │
│  tessera-core  (.NET 10, self-contained, Kestrel on 127.0.0.1:<port>)    │
│   ├─ /api/*          timeline · albums · media · search · scan · settings│
│   ├─ Scanner + LibraryWatch   read-only over the chosen folders          │
│   ├─ ThumbnailWorker          decode once → 320 px WebP + 256 px tensor  │
│   ├─ EmbeddingWorker          SigLIP2 vision (ONNX Runtime) → vector     │
│   ├─ Search                   SigLIP2 text → cosine over an in-RAM matrix│
│   └─ SQLite (WAL)   content · location · album · timeline_entry · vector │
└──────────────────────────────────────────────────────────────────────────┘
 Data dir:  %LOCALAPPDATA%\<App>\        ~/Library/Application Support/<App>/
            library.db   thumbs/ab/cd/<sha>-thumb.webp   models/   logs/
```

**Decisions, with the reason for each**

- **The web app loads from Tauri's own origin; the core is an API on
  loopback with a per-launch token.** The alternative (the core serves the
  page, as tessera-api does today) would make every Tauri plugin call a
  remote-origin capability. With this split the dialog, updater and keychain
  work as normal, the core allows CORS for the app origin only, and `<img>`
  URLs carry the token as `?sessionKey=` — which `shared/api/client.ts`
  already does for account switching. The "core serves the page" mode comes
  back in v1.1 as the LAN PWA, on a second listener the user switches on.
- **The core is a sidecar process, not a library.** It reuses the C# as it
  is, a crash in it does not take the window down, and the same binary later
  serves the LAN. Cost: process lifecycle (R-03).
- **SQLite, not Postgres.** No installer, no service, one file to back up.
  Port cost is measured in §3, not guessed.
- **Vectors live in RAM, on disk only as the source of truth.** A reference
  library is 10k–300k images. 200k × 768 dims as int8 is 150 MB; as fp16,
  300 MB. A brute-force cosine over that with `TensorPrimitives` is tens of
  milliseconds. No vector database until a real library outgrows it.
- **Embedding rides on thumbnailing.** The thumbnail worker already decodes
  every original once; the 256 px model input is produced from the same
  bitmap. One decode, two outputs, no second pass over the disk.
- **Everything the HomeLab rules protect stays protected.** Roots are opened
  read-only; trash is `trashed_at`; `POST /api/trash/empty` keeps answering
  403; a scan refuses an unmounted root; `scan_run` records
  seen/added/moved/gone/errors and the Library page shows the numbers.

---

## 3. Reuse ledger

Measured 2026-10-16 against `HomeLab/Tessera` (8,283 lines of C#, 470 of
SQL) and `tessera-web` (12,163 lines of TS/TSX).

### C#

| File | Lines | Verdict |
|---|---|---|
| `Tessera.Core`: Hashing, PathRules, ScanSafety, HashManifest, Domain/Models | ~450 | keep unchanged, with their tests |
| `Tessera.Core/ImmichId.cs` | 90 | drop |
| `Data/Db.cs`, `Migrations/SchemaMigrator.cs` | small | re-point at `Microsoft.Data.Sqlite`; keep the migration runner |
| `ContentRepository` | 261 | port: binary COPY → batched multi-row INSERT in one transaction |
| `TimelineRepository` | 93 | port: drop the `::timestamptz` casts; row-value comparison `(a,b) < (x,y)` works in SQLite |
| `AlbumRepository` | 523 | port: LATERAL joins → correlated subqueries or window functions |
| `FolderRepository` | 311 | port |
| `ViewableContentRepository` | 227 | simplify: one implicit account |
| `JobRepository`, `WorkItems`, `ExclusiveWork` | ~350 | keep for the thumbnail and embedding queues |
| `AccountRepository` | 181 | shrink to a single built-in account row (keeps every `account_id` query untouched) |
| `MusicRepository` | 298 | drop |
| `Api/Endpoints/ImmichCompatEndpoints` | 808 | keep the subset the web app calls: buckets, asset info, thumbnail, original, albums CRUD and membership, trash restore, favourites. Drop `/api/server/config`, `/api/users`. Keep Immich's shapes so the web app is not rewritten |
| `MediaEndpoints`, `ScanEndpoints` | 336 | keep |
| `AuthEndpoints`, `Auth/*` | ~200 | replace with the boot token |
| `AdminEndpoints` | 142 | replace with Settings and Library endpoints |
| `Upstream`, `Face`, `Cover`, `Music`, `AlbumIndex`, `Worker`, `Queue` endpoints | ~1,220 | drop |
| `Workers/Scanner` | 426 | keep |
| `Workers/LibraryWatch` | 319 | keep; the "bigger than 2,000 entries waits for a person" rule becomes "shows progress" — the buyer chose the folder, so the scan is the human start |
| `Workers/ThumbnailWorker` | 306 | keep; the libvips path becomes `sips` on macOS and "unsupported" on Windows |
| `Workers/Indexing`, `FaceDrain`, `MusicWorker` | 450 | drop |
| **New** | ~2,000–2,500 | EmbeddingWorker; Search (ONNX session, tokenizer, matrix, filters); model download and verify; Settings and Library endpoints; licence check; boot (port, token, CORS); `sips` adapter |

**Kept: ~5,000 lines. Dropped: ~3,300. New: ~2,300.**

### Postgres → SQLite, from the grep counts

| Construct | Where | Translation |
|---|---|---|
| binary `COPY` | 2 files | batched `INSERT … VALUES (…),(…)` in a transaction; SQLite takes >100k rows/s |
| `LATERAL` | 12 lines, 6 files | correlated subquery or `row_number() OVER` |
| `= ANY(@ids)` | 8 lines | `IN (SELECT value FROM json_each(@ids))` |
| `DISTINCT ON` | 1 line | `row_number() OVER (PARTITION BY …)` |
| `ON CONFLICT`, `RETURNING` | 30 + 11 lines | same syntax |
| 3 triggers (timeline sync, retime, album touch) | schema | SQLite triggers, same shape |
| `citext` | album name | `COLLATE NOCASE` |
| `text[]` exclude list | scan_root | JSON text |
| `timestamptz` | everywhere | `INTEGER` Unix milliseconds, UTC; the keyset index stays `(account_id, taken_at DESC, content_id DESC)` |
| `bytea` sha256 | content | `BLOB(32)` |
| 2 duplicate views | schema | same |

Roughly 300–500 lines of SQL change. The schema's ideas — content, location,
album_item on content, one denormalised `timeline_entry`, derived thumbnail
paths — do not change.

### Web app

| Feature folder | Verdict |
|---|---|
| `timeline`, `viewer`, `trash`, `selection`, `shared/api` | keep |
| `albums` | keep list, detail, cover pick from album; drop Index, move and merge across accounts |
| `search` | keep the page; the backend is now local |
| `auth` | drop login and the account menu; the boot token is injected once |
| `admin`, `faces`, `music`, `covers` | drop |
| `admin/JobsPage` | becomes the **Library** page: folders, scan and embedding progress, counts, drift numbers |
| **new** | onboarding (pick folders, model download), licence dialog, settings |
| `@immich/sdk` dependency | remove; it is used for types — define them locally |

**Kept: ~8,000 lines. New: ~1,500.**

---

## 4. On-device search

| | HomeLab Foundation | Desktop v1 default | Desktop "quality" option (Q-06) |
|---|---|---|---|
| Model | `ViT-SO400M-16-SigLIP2-384` (open_clip) | `siglip2-base-patch16-256` | `siglip2-so400m-patch16-256` |
| Dims | 1152 | 768 | 1152 |
| Weights, fp16 | ~860 MB | ~190 MB | ~860 MB |
| Runs on | RTX 5060 Ti, started by a person | the buyer's CPU; DirectML on a Windows GPU; CoreML on Apple Silicon | same |
| Licence | Apache 2.0 | Apache 2.0 | Apache 2.0 |

**Pipeline**

1. **Export** the two towers as separate ONNX files (vision, text) with the
   Optimum split export the transformers.js scripts use, fp16, opset 17.
   Scripts live in the product repo under `models/`; the manifest carries
   each file's sha256.
2. **Distribute** models from the product's GitHub Releases, downloaded on
   first run and verified. The installer stays small; a bad download is
   re-fetched, never trusted.
3. **Preprocess** in the thumbnail worker from the already-decoded bitmap:
   squash-resize to 256×256 (SigLIP2 does not centre-crop), `(x/255 − 0.5) /
   0.5`, NCHW float.
4. **Embed** through `Microsoft.ML.OnnxRuntime`: CPU everywhere;
   `Microsoft.ML.OnnxRuntime.DirectML` replaces the base package on
   `win-x64` and is appended when a GPU exists; CoreML is in the base
   package on macOS. Store fp16 in `embedding(content_id, model, vec)`; load
   int8 (per-vector scale) into RAM at start.
5. **Search**: tokenise with the Gemma SentencePiece model
   (`Microsoft.ML.Tokenizers`; fallback R-12), pad to 64, run the text
   tower, cosine over the matrix with `TensorPrimitives`, top-k, then apply
   album, kind and date filters against SQLite. "More like this" is the same
   call with the image's own vector.

**Numbers to measure in S1, not assume:** images per second on an 8-core x86
CPU, on DirectML, on an M-series; recall of int8 against fp32 on 1,000
images; the quality gap between base and so400m on a real reference
library. Working expectation: base at 10–20 img/s on CPU, so a 50k library
embeds in 1–2 hours unattended and under 30 minutes on Apple Silicon.

Foundation's trained concepts (cover appeal, pairs) do not transfer to a
different checkpoint and are out of scope.

---

## 5. Shell, packaging, signing

**Rust side, under 500 lines:** spawn the core on a free loopback port with a
32-byte token passed in the environment (not on the command line, where
`ps` shows it); kill it on exit; a PID file and a heartbeat so an orphan from
a crash is found and stopped at the next start; folder dialog; keychain;
updater; single-instance.

**Packaging, to settle in S2.** Two ways to ship the .NET core:

| | `externalBin` single-file | self-contained folder in `resources`, run by path |
|---|---|---|
| Native libs (SkiaSharp, ONNX Runtime, e_sqlite3) | extracted at run time to a temp dir | sit beside the exe, each signed |
| Notarization | the extraction fights it | plain |
| Tauri plumbing | built in | `shell:allow-execute` scoped to that path |
| **Recommendation** | | **this one** |

- **macOS:** arm64 only in v1 (Q-05). Developer ID, hardened runtime,
  notarized and stapled. The core and every dylib signed with the same
  identity. Entitlements .NET needs: `allow-jit`,
  `allow-unsigned-executable-memory`, `allow-dyld-environment-variables`,
  `disable-library-validation`. HEIC through the system's `sips`.
- **Windows:** NSIS and MSI, Authenticode through the cloud signing service
  Memories Rescue is setting up, WebView2 bootstrapped, per-user install.
  DirectML. HEIC unsupported.
- **Certificates:** the Apple Developer ID and the Windows signing identity
  that Memories Rescue orders now cover every NovaByteWorks app. No new cost.
- **ffmpeg sidecar** for video thumbnails: the same packaging and signing
  work Memories Rescue does for its ffmpeg sidecar. If that lands before M3,
  v1 has video thumbnails; if not, v1.1.
- **Installer target:** under 80 MB without models.

---

## 6. Licensing, pricing, free tier

- **Gumroad licence key, the existing NovaByteWorks flow** (Memories Rescue
  C-07 / FR-45): verified on entry, re-checked every 30 days with
  `increment_uses_count=false`, status cached, outages tolerated, key in the
  OS keychain. Buyers in India through Razorpay on the MMD backend, as the
  extensions do (Q-04).
- **Free tier:** everything works up to 2,000 indexed images; past that,
  browsing continues and indexing pauses with an unlock prompt. The
  alternative is Eagle's 30-day trial; the beta decides (Q-03).
- **Price:** US$29 launch, US$39 list, one-time, all 1.x updates, two
  computers. Anchors: Eagle about $30 one-time (5.0 added AI search); Excire
  Foto 2027 $249 one-time; Mylio Photos+ around $10 a month. Q-02.
- **Bundle:** MMD lifetime buyers get a discount code. The audience overlap is
  the whole thesis.

---

## 7. Milestones

**Sequencing rule:** Memories Rescue owns 16 Oct – 30 Nov. This plan spends
that window only on validation and three one-day spikes, and starts the build
on 1 Dec. If Memories Rescue slips, this slips by the same amount; the two are
never worked in parallel.

| Milestone | Dates | Scope | Exit gate |
|---|---|---|---|
| **V0 Validate** | 17 – 31 Oct | one-question email to the two paying artists in their languages; a waitlist page on the site; a 60-second screen recording of the HomeLab app searching a reference folder | 3 "I would pay" replies from active MMD artists, **or** 25 waitlist sign-ups. Below that, stop here |
| **S1 Search spike** | 1 day in Nov | SigLIP2 base ONNX in .NET on Windows and macOS; embed 1,000 images; 20 text queries; int8 vs fp32 | ≥ 8 img/s on CPU; queries find the right references by eye; tokenizer works or R-12 fallback chosen |
| **S2 Shell spike** | 1 day in Nov | Tauri hello window + .NET core spawned, token, CORS; signed and notarized on macOS, signed on Windows | installs and runs on a clean VM of each OS |
| **S3 Data spike** | 1 day in Nov | SQLite port of content, location, timeline and the scanner over a generated 200k-file fixture | timeline page < 20 ms at any depth; 200k files scanned in < 10 min on NVMe |
| **M1 Core** | 1 – 19 Dec | repo; SQLite port; scanner; watch; thumbnails; timeline; albums; viewer; trash; favourites; duplicates view | parity with HomeLab Tessera on the fixture; Core tests green; no network calls |
| **M2 Search** | 22 Dec – 9 Jan | model download; embedding worker; text search; more-like-this; filters; progress on the Library page | a 50k library embeds unattended; search with filters |
| **M3 Product** | 12 – 30 Jan | onboarding; Library page; settings; licence and free tier; updater; log export; `sips` HEIC; ffmpeg if available | install → pick folders → search → buy, end to end, both OSes |
| **M4 Beta** | 2 – 20 Feb | 10–20 testers from the waitlist and the artists; **freeze 6 Feb**; signed builds; fixes | no Sev-1; three testers past 20k images; free-tier decision made |
| **M5 Launch** | 23 Feb – 6 Mar 2027 | site page; Gumroad product; demo video; privacy page; launch posts in the art communities that already know MMD | live on the site and Gumroad |

About eleven weeks of solo build after 1 Dec.

---

## 8. Risks

| # | Risk | Mitigation |
|---|---|---|
| R-01 | The base model's search is not good enough on real reference material | S1 gate on a real folder; so400m as the "quality" option |
| R-02 | Eagle 5.0 ships AI search at the same price | position on what Eagle cannot do: in-place, content identity, duplicates, moves are free, offline model you own |
| R-03 | Orphaned or wedged core process | PID file, heartbeat, kill on exit, "restart engine" button |
| R-04 | Notarizing a .NET sidecar | S2 in November, Memories Rescue's lessons, the resources-folder route |
| R-05 | Model download fails or is tampered with | GitHub Releases, sha256 manifest, resumable, retry |
| R-06 | Windows SmartScreen on a new publisher | signed betas from M4 build reputation |
| R-07 | RAM for vectors on big libraries | int8, a stated ceiling, usearch on disk as the upgrade |
| R-08 | HEIC on Windows and videos without ffmpeg | say so on the product page; both are v1.x items |
| R-09 | Attracting the wrong audience | no face features; the copy says reference library and personal study, never "download anyone's photos" |
| R-10 | Time against Memories Rescue | the sequencing rule in §7 |
| R-11 | Intel Mac buyers | Q-05; an x64 build is a second publish target, not a redesign |
| R-12 | Gemma SentencePiece in `Microsoft.ML.Tokenizers` | fallback: onnxruntime-extensions' SentencePiece operator inside the text ONNX graph |

---

## 9. Open questions

| # | Question | Decide by |
|---|---|---|
| Q-01 | Product name (several things are already called Tessera) | V0 |
| Q-02 | Launch and list price | M4 |
| Q-03 | Free tier (2,000 images) or 30-day trial | M4 |
| Q-04 | Razorpay route for buyers in India | M3 |
| Q-05 | macOS Intel build in v1 | S2 |
| Q-06 | Ship so400m as a downloadable option | S1 |
| Q-07 | LAN PWA in v1.1 or v2 | M5 |
| Q-08 | Telemetry: none, or opt-in crash reports only | M3 |
| Q-09 | Public SRS repo like Memories Rescue, or this plan only | V0 |

---

## 10. Repository

```
tessera-desktop/            private, arbaaz360 (name follows Q-01)
  src-tauri/                the Rust shell
  web/                      fork of tessera-web, trimmed per §3
  core/                     fork of Tessera.Core, .Data, .Api → Tessera.Desktop.*
  models/                   export scripts, manifest with sha256s
  fixtures/                 generator for the 200k-file library
  docs/                     this plan, DECISIONS.md
```

**Fork, not a shared package.** The HomeLab service and the product diverge
on storage and workers from day one. `Tessera.Core` becomes a NuGet package
only when the same fix has to land in both twice.

---

## 11. This week, about three hours

1. Email the two paying artists, one question each, in Spanish and Arabic.
2. Put a waitlist page on the site under the MMD product family.
3. Record the HomeLab app searching one reference folder for the page.
4. Book the three spike days in November around Memories Rescue.
5. Order nothing: the certificates arrive with Memories Rescue.

---

### Sources consulted

- Eagle pricing and the 5.0 AI features: [Capterra](https://www.capterra.co.uk/software/184384/eagle), [Eagle blog](https://en.eagle.cool/blog)
- Excire Foto 2027 pricing: [The Dead Pixels Society](https://thedeadpixelssociety.com/prc-debutes-excire-foto-2027-ai-powered-photo-management-app/)
- Mylio sync and pricing: [Mylio](https://mylio.com/features/private-media-sync/), [Subger](https://subger.com/en/service/mylio-photos)
- ONNX Runtime packages (CoreML in the base package, DirectML separate): [NuGet](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime), [DirectML EP](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)
- SigLIP2 split ONNX export: [AXERA-TECH export script](https://huggingface.co/AXERA-TECH/siglip2-base-patch16-224/blob/main/python/export_onnx.py), [transformers.js SigLIP config](https://huggingface.co/spaces/rnnandi/convert-gemma3-to-onnx/blob/main/transformers.js/scripts/extra/siglip.py)
- Tauri 2 sidecars and .NET notarization: [sidecar lessons](https://dev.to/chenxxpro/bundling-a-cli-binary-as-a-tauri-v2-sidecar-lessons-from-building-a-desktop-app-5po), [.NET macOS notarization](https://learn.microsoft.com/dotnet/core/install/macos-notarization-issues)
- Google Photos API restriction (why no cloud route): [Google developers blog](https://developers.googleblog.com/en/google-photos-picker-api-launch-and-library-api-updates/)
