# UX review: every flow, as a first-time buyer

Written 2026-10-18 by the ux-review session. Code from `main` at `a9e371c`.
Screenshots in [`docs/ux/`](ux/). No code was changed.

## Verdict

Under the hood Tessera is in good shape. Search by description puts the
sketches, colours and textures first, "More like this" works, albums can be
created, renamed, given a cover and deleted, trash and restore work, the
licence unlock works, and all of it survives a restart. The dialogs are
written in plain, honest English. But walked as whole flows, the way a buyer
uses it, it does not yet feel like a desktop photo app; it feels like a
capable web dashboard. Three things break trust outright: **one unreadable
file (a corrupt JPEG or a TIFF) blanks its whole month, album or search with
no message**; **two text searches at the same moment crash the engine**; and
**a picture cannot be taken out of an album**, because the only button on
offer hides it from the whole library. Around these, the habits people bring
from Eagle, Lightroom, Google Photos and Windows Photos are missing: no
right-click anywhere, Ctrl+A selects the page's text, the viewer cannot zoom,
stops at the end of each month and cannot show the file in Explorer,
favourites cannot be listed, duplicates are a list of hashes, nothing can be
undone, and much of the wording ("siglip2-base-patch16-256", "root",
"queue", "See logs/ in the data dir") is written for the engineer. None of
this needs a redesign: fix the three blockers, then selection and the viewer,
and it will feel like a product.

| Severity | Count |
|---|---|
| Blocker | 3 |
| Major | 13 |
| Minor | 14 |
| Polish | 5 |
| **Total** | **35** |

## The five changes that would most improve how it feels

1. **No file can blank a view** (B1, M10). Make the core's row mapping
   accept pictures with unknown dimensions, give the grid a per-month
   "Couldn't load · Retry", and list unreadable files with their reason on
   the Library page.
2. **Search cannot crash the engine** (B2). Serialise text embedding on
   DirectML. Then make results honest: a relevance floor so nonsense finds
   nothing (M7), and a "searching 3,100 of 4,217" line while indexing (M13).
3. **Selection and albums behave like a desktop app** (B3, M3, M4, M11).
   One right-click menu on tiles, albums and folders; Ctrl, Shift, Ctrl+A,
   Esc and Delete; "Remove from album"; "New album…" in the picker; an
   Undo toast instead of a confirm.
4. **A viewer made for references** (M1, M2). Page through the whole
   library or album, zoom and pan to 100 %, "Show in Explorer" and "Copy
   path".
5. **Speak the buyer's language and finish the half-built views** (M5, M6,
   M8, M9, m1–m4). A Favourites view, Duplicates with pictures, a lock on
   capped tiles, a placeholder for videos, a Library page in sentences, and
   no model ids, forward-slash paths or log hints on screen.

## Findings, ranked

Flows: 1 first run, 2 first folder and indexing, 3 grid, 4 viewer,
5 selection, 6 albums, 7 search, 8 duplicates/trash/favourites, 9 Library
page, 10 settings/licence/cap/restart.

### Blockers

| # | Flow | What happened | What a person expects | Suggested fix | Effort | Lives in | Evidence |
|---|---|---|---|---|---|---|---|
| B1 | 3, 6, 7 | A folder holding one corrupt JPEG and one TIFF: their month ("October 2026 · 49") is a **blank block about 3,500 px tall at 1280×800, 5,684 px at 900×600**, the first thing on Pictures, with no message. The "Formats" album is blank the same way. A file-name search for "gradient" says "Search failed — Internal error. See logs/ in the data dir." Cause: `GET /api/timeline/bucket` and `/api/search` answer 500 whenever one answer mixes pictures with and without known dimensions (Dapper: `Error parsing column 1 (Width=320 - Int64)`, the `COALESCE(c.width, th.width)` column in `AssetShapes.Columns`), and the grid draws nothing for a month whose fetch failed | Every picture that can be shown is shown; a file that cannot be read is one tile that says so | Core: read width and height as `long?` (or `CAST` in the SQL) and add a test with a NULL-dimension row inside a bucket. Web: a per-month "Couldn't load this month · Retry" in `TimelineGrid` | S | `core/`, `web/` | [blank month](ux/b1-blank-month.jpg), [search error](ux/b1-search-internal-error.jpg) |
| B2 | 7 | **Text searches crash the engine**: access violation `0xC0000005` in `InferenceSession.Run`, called from `SiglipEmbedder.EmbedTexts`, on DirectML (RTX 5060 Ti). With plain API calls, two simultaneous searches killed it on the third pair, six at once on the first round; 20 one-at-a-time searches, and 40 run while 400 new pictures were embedding, were all fine. In the dev build every search page sends two requests (React StrictMode), and the UI crashed it in 3 of 4 description searches. In the release build it takes two searches within milliseconds; seven quick filter clicks did not do it. The page then says "Search failed — Failed to fetch" | Searching never takes the app down | Serialise `Run` per session on DirectML (a lock or `SemaphoreSlim` around `SiglipEmbedder.Run`). ONNX Runtime's DirectML provider does not support concurrent `Run` calls on one session. Add a two-thread test | S | `search/` | [search failed](ux/b2-search-engine-crashed.jpg) |
| B3 | 6 | Inside an album, a selection offers Favourite, Unfavourite, Add to album, Move to trash, Clear. **There is no "Remove from album"**, in the bar, the viewer or anywhere else in the app. The only removal on screen hides the file from the whole library | Remove from album (Delete inside an album, right-click, the viewer), with undo | Add it to the selection bar and the viewer when inside an album. The core already answers `DELETE /api/albums/{id}/assets` | S | `web/` | [album selection](ux/b3-album-no-remove.jpg) |

### Major

| # | Flow | What happened | What a person expects | Suggested fix | Effort | Lives in | Evidence |
|---|---|---|---|---|---|---|---|
| M1 | 4 | The viewer pages only inside the month it was opened from: "1 / 3", and → at "3 / 3" does nothing | The arrows walk the whole timeline or album, as in Photos and Lightroom | Give the viewer a cursor over the whole query and load the next month at the edge. `PhotosPage.tsx` hands it one month's pictures | M | `web/` | [1 / 3](ux/m01-viewer-month-edge.jpg) |
| M2 | 4 | No zoom or pan (wheel, double-click, +/−, 100 %). The path is shown but cannot be used: no Show in Explorer, Open with, Copy path or drag out to Photoshop or PureRef. At 900×600 the info panel, open by default, takes over a third of the width | Artists study detail: zoom to 100 % and pan; jump to the file on disk. Eagle and Lightroom both do | Wheel and double-click zoom with pan and a 1:1 key; a `reveal_path` shell command (new in contract §5, so the coordinator decides) plus Copy path; info panel closed by default below about 1,100 px | M | `web/`, `src-tauri/` | [viewer at 900×600](ux/m02-viewer-900x600.jpg) |
| M3 | 5 | The grid acts like a web page. **Ctrl+A highlights every label on the page.** Ctrl+click opens the viewer instead of starting a selection. Shift+click only reaches within one month (February to January gave 2 selected, not 3). Esc and Delete do nothing to a selection. No Select all. No right-click menu on tiles, albums or folders | Explorer and Eagle rules: Ctrl and Shift extend, Ctrl+A selects all, Esc clears, Delete trashes, right-click lists the actions | A grid keyboard handler (Ctrl+A, Esc, Delete, arrows), Ctrl+click toggles, Shift ranges across months, one context-menu component reused on tiles, albums and folders (its `preventDefault` also hides the webview's own menu) | M | `web/` | [Ctrl+A](ux/m03-ctrl-a-selects-text.jpg) |
| M4 | 5, 6 | "Add to album" cannot create one: with only folder albums, the picker lists only folders. It also accepts them: adding a screenshot to "Sketches" says "Added 1 to Sketches", and that folder's album no longer matches the folder | "Add to → New album…" as in Google Photos and Lightroom; an album that mirrors a folder is not edited by hand | A "New album…" row at the top of the picker (create, then add); my albums listed first; folder albums read-only, or a clear word on what adding means | S, M | `web/`, `core/` | [picker](ux/m04-picker-no-new-album.jpg), [added to a folder](ux/m04-added-to-folder-album.jpg) |
| M5 | 8 | F and the star mark a favourite and the tile shows a star, but **there is no Favourites view**, filter or sidebar entry | A Favourites place one click away | A sidebar "Favourites" using the timeline's existing `isFavorite` filter | S | `web/` | [starred, nowhere to list](ux/m05-favourite-no-view.jpg) |
| M6 | 8 | Duplicates is text at the foot of the Library page: "2 copies · 46.8 KB · fa094385a628" and two monospace paths. No pictures, cannot open either copy, cannot reveal one | A visual view: the picture once, each copy with its folder, open or reveal each | A Duplicates page with a thumbnail per group and Show in Explorer per copy; keep "nothing is deleted" | M | `web/` | [duplicates](ux/m06-duplicates-text-list.jpg) |
| M7 | 7 | Every description search says "100 results". "astronaut riding a giraffe" returns 100 sketches. There is never a "nothing matches", and the tail of every list is noise | Results stop when they stop matching | A score floor calibrated on the test set, or a "Best matches / Less likely" divider, with the count above the floor | M | `core/`, `web/` | [nonsense query](ux/m07-nonsense-100-results.jpg) |
| M8 | 10 | Past the 2,000 cap the extra pictures sit in the grid as plain grey tiles among real thumbnails, with no lock and no reason. "More like this" on one says "Search failed — That picture has no thumbnail to compare from yet" beside a broken-image chip. (The banner and the sidebar do explain the cap well) | Locked pictures look locked and say how to unlock | A lock badge and "Unlock to see" on capped tiles (a per-item flag from the core, or the licence flag plus the missing thumbnail); no More like this on them | S | `web/`, `core/` | [grey tiles](ux/m08-capped-grey-tiles.jpg), [More like this](ux/m08-capped-more-like-this.jpg) |
| M9 | 3, 6 | Videos are blank grey tiles with only a duration, no ▶. The "Videos" folder album's cover is the broken-image icon | A frame of the video, or at least a film placeholder with ▶ | Now: a video placeholder tile and a cover fallback. Later: ffmpeg thumbnails, already in the core's Not done | S, L | `web/`, `core/` | [video tiles](ux/m09-video-grey-tiles.jpg), [broken cover](ux/m09-video-album-broken-cover.jpg) |
| M10 | 9 | Files Tessera cannot read are silent. The TIFF (the plan promises TIFF on every platform) and the corrupt JPEG appear nowhere; the scan reports errors 0; Thumbnails reads "2,313 done · 0 pending" while 2 failed (the core sends `failed: 2`, the page drops it) | "2 files can't be shown: gradient.tif (TIFF not supported on Windows yet), broken.jpg (damaged)", each with Show in Explorer | Show `failed` on the Library page with names and reasons (a core route listing failed items); decide TIFF on Windows | M | `core/`, `web/` | [Library](ux/m10-library-hides-failures.jpg) |
| M11 | 5, 6, 8 | Nothing can be undone. Every single trash asks "Move to trash?" and waits on "Working…"; adding to an album, favouriting and deleting an album have no undo | Act, then "Moved to trash · Undo", as in Google Photos and Lightroom | One toast with Undo for trash, add or remove from album, favourite; drop the confirm for trash, which is reversible | M | `web/` | [confirm](ux/m11-trash-confirm-no-undo.jpg) |
| M12 | 6 | Nested folders collapse into the top-level album: `Projects/2024/Client work/Moodboard - autumn campaign/…` sits in album "Projects · 1 item", and "Moodboard - autumn campaign" is not an album. Onboarding says "Every folder inside it becomes an album" | Reference libraries are nested (Anatomy/Hands, Anatomy/Feet); Eagle shows the tree | An album per folder at every depth, or a folder tree in the sidebar, named by path where names repeat | M | `core/`, `web/` | [flattened](ux/m12-nested-folder-flattened.jpg) |
| M13 | 2, 7 | Outside the Library page nothing says the app is still working. With about 1,100 of 4,217 pictures not yet thumbnailed or embedded, a search answered "100 results" with no hint that a quarter of the library was not searchable yet; the sidebar said only "4,217 pictures · 5 folders" | "Indexing 1,100 pictures…" in the corner, and "Searching 3,100 of 4,217 pictures" until it is done | An activity line in the sidebar footer from `/api/library`, and a coverage line on the search page from `/api/search/status` | S | `web/` | [search while indexing](ux/m13-search-while-indexing.jpg) |

### Minor

| # | Flow | What happened | What a person expects | Suggested fix | Effort | Lives in |
|---|---|---|---|---|---|---|
| m1 | 1, 7, 9 | Errors written for the engineer: "C:/TesseraTest/Nope is not a directory", "… is inside the root …", "Internal error. See logs/ in the data dir.", "Failed to fetch", and on the start screen "has not answered /health/ready yet" with the engine's URL | "That folder doesn't exist", "Already in your library, inside References", "Something went wrong · Try again" | One message map in `tessera.ts`; raw detail behind a Details link | S | `web/` |
| m2 | 7, 9, 10 | Engineer words on buyer screens: "ranked by siglip2-base-patch16-256", "RUNS ON directml", "Watch · Last tick", "releases the queue; nothing is rescanned", "pictures are recorded", "Albums keep their members" | "Search by description", "Uses your graphics card", "Last checked 4:35 PM" | A wording pass; the model id only in About | S | `web/` |
| m3 | 1, 4, 9 | Windows paths shown with forward slashes, in monospace: `C:/TesseraTest/Review/References` | `C:\TesseraTest\Review\References` | Show paths in the platform's spelling | S | `web/` |
| m4 | 9 | The folder card's numbers do not explain themselves: FILES 194, PICTURES 189, ADDED 189 (5 are copies; nothing says so); MOVED and GONE are last-scan counts in the same row as totals; the Search index bar is not full while it says "0 pending" | One sentence per folder: "189 pictures (5 are copies) · checked 4:13 PM · nothing changed" | Sentences first, the numbers in a details row | S | `web/` |
| m5 | 1, 2 | The 559 MB model download starts the moment the engine starts, before the welcome page says so. Once shown, the bar is per file and fills three times ("312 MB of 352 MB · text.onnx"); the total is never shown while downloading | "Downloading the search model: 320 of 559 MB", one bar, mentioned before it starts | One bar over the manifest's total; a line on the welcome page ([seen here](ux/onboarding-indexing.png)) | S | `web/`, `core/` |
| m6 | 6 | My own album is buried: among 19 folder albums "Warm palette refs" falls below the fold. List view has no covers and every album says "Oct 2026". The "Any size / 10+ / 30+ / 100+" chips are noise at this scale | My albums first, folders apart | Two sections, My albums and Folders; default sort by name or last added | S | `web/` |
| m7 | 3 | No way to jump to a year across six years of months (no date scrubber); coming back to Pictures resets the scroll to the top; no tile size control (three columns of very large tiles at 900 px) | Google Photos' scrubber; Eagle's and Lightroom's size slider | A year scrubber on the scrollbar, remembered scroll, Ctrl+wheel or a slider for tile size | M | `web/` |
| m8 | 4 | A 240×180 picture is stretched to fill the viewer; file names break mid-extension ("…091700.pn" / "g"); a video starts playing with sound | Small pictures at 100 %; names wrap between words; video starts muted or paused | Cap the scale at 1; `overflow-wrap: break-word`; muted autoplay | S | `web/` |
| m9 | 6 | The cover picker does not mark the current cover and stays open after "Cover set."; the viewer has no "Use as cover" | Pick, and it closes; the current cover is outlined | Close on pick, outline the current one, add the viewer action | S | `web/` |
| m10 | 5, 6 | The album picker filters as you type, but Enter does nothing and the arrow keys do not move | Type, then Enter | Enter takes the first match; arrows move | S | `web/` |
| m11 | 7 | File-name search: tiles carry no names; "Nothing found" advises "Try describing the scene differently"; after a reload the search box is empty while the old query's results are shown | Names under tiles in name mode, advice that fits the mode, the box shows the active query | As described | S | `web/` |
| m12 | 8 | Trash: "Restore all" stays on an empty trash; restoring one picture is only possible from the viewer, no multi-select; "Trash" suggests deletion, yet nothing can be deleted and there is no Show in Explorer to do it by hand | Select several and restore; a name that says what it is | Selection in Trash; consider "Hidden"; pair with Show in Explorer (M2) | S | `web/` |
| m13 | 10 | Settings reads like a config file: "Thumbnail workers [4]", a MODEL / RUNS ON / INDEXED / STATE table, the data folder as plain text, no Open data folder, no Export logs (the plan lists log export). "One licence, once, for this machine" may read as one computer only | A few settings in plain words; Open data folder; Export logs for support | Workers under Advanced; Open folder and Export logs as shell commands; check the licence line against the Gumroad terms | M | `web/`, `src-tauri/` |
| m14 | 3 | References without EXIF are dated by their file time, so most of a downloaded library gathers in the month it was copied (49 in "October 2026" here), and the grid sorts only by date taken | Eagle sorts by date added, name and size | Sort by date added and by name beside Newest first | M | `web/`, `core/` |

### Polish

| # | Flow | What happened | Suggested fix | Effort | Lives in |
|---|---|---|---|---|---|
| p1 | all | The logo is the letter "T" in an orange square, which reads as a placeholder | A real mark | S | `web/` |
| p2 | all | Twice, two sidebar items looked selected at once (Pictures with Albums, Pictures with Search) after moving between pages | One active item | S | `web/` |
| p3 | 5 | The selected check is a small orange dot, hard to see on colourful pictures; the bar has two unlabeled stars (Favourite, Unfavourite) | A clear check and outline; one labeled toggle | S | `web/` |
| p4 | 3 | The sticky month header leaves a translucent band over the first row while scrolling | Opaque header or a soft shadow | S | `web/` |
| p5 | 6 | Album list view has no covers | A small cover per row | S | `web/` |

## What works

| Flow | Good |
|---|---|
| 1, 2 | Three clear promises on the welcome page; the first folder scanned 194 files in 0.6 s, and the page showed it as numbers moving, then opened the grid on its own |
| 6 | Create, inline rename (click the title), cover pick, delete with a dialog that says the files are untouched, and a duplicate-name check that offers to open the existing album |
| 7 | Ranking: "pencil sketch on white paper" puts every sketch first, "green noise texture" the green textures, "orange square with a number" the orange squares; "More like this" is instant and right; the empty state teaches by example |
| 8 | Trash is a mark and says so; restore from the viewer puts the picture back with its star |
| 9 | Remove folder explains exactly what happens; adding a folder inside an existing one is refused |
| 10 | The [cap banner](ux/cap-banner.jpg) and sidebar line carry counts; unlock releases the queue at once; licence, albums, cover and favourites all survive an engine restart |

## How it was run

| What | How |
|---|---|
| Engine | `tessera-core` published from `a9e371c` to a temp folder, a temp `TESSERA_DATA_DIR`, fixed port, the real model downloaded (559 MB), DirectML on the RTX 5060 Ti |
| App | `web/` from the same commit, first with Vite dev, then as a production build (`vite build`, `vite preview`), because dev mode doubles every request. Port 5180 with `TESSERA_WEB_ORIGIN=http://localhost:5180`, because another session held 5173 |
| Window | Built-in browser at 1280×800 in dark and light, and at 900×600 |
| Pictures | Test pictures only, nothing from the owner's folders: 194 generated files in `C:\TesseraTest\Review\References` (sketches, colour studies, textures, screenshots dated by name, two MP4s, a GIF, BMP, WebP and TIFF, one corrupt JPEG, a 6000×4000 JPEG, a deep folder with a long name, 5 duplicated files), the 31 in `C:\TesseraTest\Library`, 2,100 small numbered JPEGs to cross the cap, then 1,900 more to watch indexing. 4,217 pictures in 5 folders at the end |

To see B1 again: put any damaged `.jpg` and a `.tif` beside ordinary pictures
in one folder, add it, and open Pictures or that folder's album.

**Not judged here**, because they exist only in the Tauri window: the system
folder picker, the webview's own right-click menu (nothing in
`tauri.conf.json` turns it off; not seen), "Save a copy" and "Copy image" in
the webview, the shell restarting a crashed engine, Gumroad activation. Also
not covered: macOS, HEIC, a first run with no network. Desktop control was not
used: those flows need the real app, and the owner's installed copy was
running and was to be left alone.

**Left out on purpose**, because another session is fixing them: drag and
drop, the Add pictures button, the Tessera-owned folder, the stale album grid,
the album page's second search box, leftover folder albums.
