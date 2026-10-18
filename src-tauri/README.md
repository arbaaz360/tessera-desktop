# src-tauri: the shell

The Tauri 2 app around Tessera Desktop. It owns the window, starts and
supervises the core, answers the commands in `docs/CONTRACT.md` section 5,
activates licences, and is what CI packages.

| File | Does |
|---|---|
| `src/lib.rs` | builds the app: plugins, commands, setup, exit |
| `src/core.rs` | core lifecycle: stale pid, spawn, ready line, supervise, stop |
| `src/commands.rs` | the contract's commands, plus `core_restart` and `open_logs` for the error page |
| `src/licence.rs` | Gumroad verify, keychain, `licence.json`, `PUT /api/licence` |
| `src/updater.rs` | update check at launch, inactive until a real public key is configured |
| `src/applog.rs` | the shell log, with the core's stderr interleaved |
| `src/paths.rs` | data dir, logs, core executable |
| `src/import.rs` | the file picker's filter (the scanner's extensions) and the drop log line |
| `src/error-page.js` | what the window shows when the shell gives up on the core |
| `tauri.conf.json` | the app config from the contract |
| `tauri.stub.conf.json` | merged with `--config`: static dev page instead of the web app |
| `capabilities/default.json` | `core:default` only; no plugin is exposed to JavaScript |
| `tools/stub-core/` | a stand-in core for testing the shell on its own |
| `tools/smoke/` | the CI smoke harness: generated pictures, the model download, search on GPU and CPU, the real core through the handshake |
| `dev-page/` | a page that calls every command and prints the answers |

## Build and test

Rust is at `~/.cargo/bin`. In Git Bash: `export PATH="$HOME/.cargo/bin:$PATH"`.

The build embeds `resources/core/**/*` and refuses an empty folder, so put a
core there first: the stub for shell work, the real publish for the app.

```bash
bash src-tauri/tools/use-stub.sh          # builds the stub, copies it to resources/core
cd src-tauri
export TAURI_CONFIG="$(cat tauri.stub.conf.json)"   # codegen embeds dev-page/, not web/dist
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test
```

`TAURI_CONFIG` is only needed while `web/dist` does not exist. The tests
cover the ready-line parser, the stale-pid logic (fake process table) and the
Gumroad mapping (fake server on loopback). They touch neither the keychain
nor the network.

## Run with the stub

```bash
bash src-tauri/tools/use-stub.sh
cd src-tauri
pnpm dlx @tauri-apps/cli@2 dev --config tauri.stub.conf.json --no-watch --no-dev-server
```

`--no-dev-server` matters: without it `tauri dev` serves the static page from
its own server on `http://127.0.0.1:1430`, the page's origin is that, and the
core (which allows `http://tauri.localhost` only) refuses its requests with
CORS. With the flag the page loads from the app's own origin, as in a release
build. The real web app in `tauri dev` loads from `http://localhost:5173`,
which the core allows because of `TESSERA_DEV=1`.

The window shows the dev page. On load it calls `app_version`, `licence_get`
and `core_info`, then `GET /api/licence` on the core with the token. Every
other command has a button. The log (below) shows the handshake and every
request the stub served.

The stub reads two extra variables, for lifecycle tests:

| Variable | Effect |
|---|---|
| `STUB_IGNORE_STDIN=1` | does not exit on stdin EOF, so it outlives the shell (stale pid test) |
| `STUB_CRASH_AFTER_MS=n` | exits with code 7 after n ms (restart policy test) |

Set `TESSERA_DATA_DIR` in the shell's environment to keep a test run's data
away from the real data dir.

## Run with the real core

```bash
dotnet publish core/src/Tessera.Api -c Release -r win-x64 --self-contained -o src-tauri/resources/core   # osx-arm64 on macOS
pnpm --dir web install
cd src-tauri
pnpm dlx @tauri-apps/cli@2 dev      # starts Vite on :5173 and the window
pnpm dlx @tauri-apps/cli@2 build    # web build, then NSIS + MSI (Windows) or DMG (macOS)
```

Installers land in `src-tauri/target/release/bundle/`.

## Adding pictures

| Piece | What it does |
|---|---|
| drag and drop | Tauri 2's handler, on for the main window (`dragDropEnabled`). Files dragged from File Explorer or Finder reach the web app as `tauri://drag-enter/over/drop/leave` with their paths and the pointer position; the web app picks the target (an album or the library) and calls the core's `POST /api/import` |
| `pick_files()` | the dialog plugin's file picker, several at once, filtered to pictures and videos; absolute paths or `null` |
| log | one line per drop, in counts: `drop: 5 paths: 4 files (4 pictures or videos), 1 folders, 0 not found` |

Positions: on Windows Tauri reports client pixels (physical); on macOS it
reports the view's points under the same "physical" label. The web app
converts per platform (`web/src/features/import/dropTarget.ts`).

### Run a second copy beside the installed app

The single-instance plugin keys on the identifier, and WebView2 keeps its
profile under it, so a test build with another identifier runs beside the
installed app without touching it. Point the data dir and the Tessera folder
at a scratch folder:

```bash
pnpm dlx @tauri-apps/cli@2 build --no-bundle --config '{"identifier":"com.novabyteworks.tesseradesktop.dev"}'
TESSERA_DATA_DIR=/tmp/td-data TESSERA_FOLDER=/tmp/td-Tessera ./target/release/tessera-desktop.exe
```

`TESSERA_FOLDER` passes through to the core (the shell does not clear its
environment); without it a copy dropped from outside lands in the real
`Pictures/Tessera`.

### Checked in a running copy (Windows, 2026-10-18)

A release build with the identifier above, beside the installed app (which
kept running, untouched), a scratch data dir, search off. The drops were
Tauri's own `tauri://drag-*` events emitted inside the window (paths and a
position, the payload wry hands over), so everything from the event on is
the shipped code: the web app, the shell's commands, the core.

| Check | Result |
|---|---|
| drag over the album card "Trips", then over empty space, then away | overlay "Drop to add 7 items to “Trips”", the card outlined; then "…to your library"; then gone |
| drop of 5 loose files, a folder, and a file already in a library folder, on "Trips" | 5 copied to `Tessera/Trips/` (originals in place), the folder became a library folder in place (2 pictures, nothing copied), the known file "already"; "Trips" 0 → 8 items |
| a new file onto the card | toast "Adding 1 item…", then "1 copied in"; the card's count 8 → 9 within 1.5 s; the toast gone after about 8 s |
| a known file and a missing path onto the album page | "1 already there · 1 skipped", "missing.png: not found"; that toast stays until dismissed |
| a file onto the grid | copied to `Tessera/Unsorted/`, no album |
| the album page | one search box (the top bar's, scoped to the album), Add pictures and Add folder |
| removing the library folder | its folder album "Inside" went; "Trips" stayed, 9 → 8 live (the picture from that folder no longer counts); core log: `Root 1 removed; 1 folder albums it left with no live picture went with it` |
| window closed | core stopped, exit code 0, `core.pid` gone |

## What the core gets

The shell runs `resources/core/tessera-core(.exe)` from the app's resource
dir, with the core's folder as working dir, stdin piped and held open,
stdout read for the ready line, stderr copied into the shell log.

| Variable | Value |
|---|---|
| `TESSERA_TOKEN` | 32 random bytes, base64url, 43 characters, new on every start |
| `TESSERA_DATA_DIR` | `%LOCALAPPDATA%\TesseraDesktop` or `~/Library/Application Support/TesseraDesktop` (or the shell's own `TESSERA_DATA_DIR`) |
| `TESSERA_WEB_ORIGIN` | `http://tauri.localhost` on Windows, `tauri://localhost` on macOS |
| `TESSERA_DEV` | `1` in debug builds, absent in release |

Lifecycle, in numbers:

| Event | What the shell does |
|---|---|
| start | reads `core.pid`; a live pid whose executable is named `tessera-core` is killed (5 s to die); the file is removed |
| ready | waits 30 s for the ready line; otherwise kills the child and gives up |
| core ready | `PUT /api/licence` with the stored state; re-checks the key with Gumroad if the last check is 30 days old |
| core exits unexpectedly | restarts it; after 3 restarts within 60 s it gives up |
| gave up | the window shows an error page with the log path and a "Restart engine" button |
| window closed / app exit | closes stdin, waits 5 s, kills |

`core_info()` waits for the ready line, so the web app can call it at once.

## Licence

| Step | Where |
|---|---|
| verify | `POST https://api.gumroad.com/v2/licenses/verify`, `product_id` from `TESSERA_GUMROAD_PRODUCT_ID` at build time (placeholder until the product exists), `increment_uses_count=true` on activation, `false` on re-check |
| key | OS keychain, service `TesseraDesktop`, user `licence-key` |
| state | `<data>/licence.json`: `state`, `checkedAt`, `keyMasked` |
| core | `PUT /api/licence {"state": …}` with the bearer token, again on every core start |

`licence_activate` answers `error: "invalid"` for a well-formed no from
Gumroad (unknown key, refunded, charged back) and `"offline"` for a transport
error, a 429 or a 5xx; the state stays as it was. A re-check that gets a
well-formed no reverts to `free`; one that gets no answer changes nothing.

## Logs

`<data>/logs/shell-YYYY-MM-DD.log`. One line per event, UTC. Lines from the
core's stderr carry the target `core`. In debug builds everything also goes
to the shell's stderr (the terminal running `tauri dev`).

## Events the shell emits

| Event | Payload | When |
|---|---|---|
| `core-ready` | `{baseUrl, token}` | a core is up (first start and every restart) |
| `core-error` | `{message, logPath}` | the shell gave up on the core |
| `update-available` | `{version}` | an update exists (only with a real updater key) |

## CI

`.github/workflows/build.yml`. Nothing has run on a real Mac: GitHub's
`macos-latest` runner (Apple silicon, 3 cores, macOS 26) is the only one.

| Job | Runs on | Does |
|---|---|---|
| `shell` | Windows, macOS | stub core, `cargo fmt --check`, `clippy -D warnings`, `cargo test` |
| `bundle` | Windows (`win-x64`), macOS (`osx-arm64`) | publishes the core into `resources/core` (prints files and MB), builds the web app, `tauri build`, uploads the installers. On macOS it then launches the built `.app` (below) |
| `smoke` | macOS | the parts on a Mac without the shell (below). Numbers go to the job summary |

Signing steps exist and run only when their secrets exist: `APPLE_CERTIFICATE`,
`APPLE_CERTIFICATE_PASSWORD`, `APPLE_SIGNING_IDENTITY`, `APPLE_ID`,
`APPLE_PASSWORD`, `APPLE_TEAM_ID` (Tauri's own names); `AZURE_CLIENT_ID`,
`AZURE_CLIENT_SECRET`, `AZURE_TENANT_ID`, `AZURE_ENDPOINT`,
`AZURE_CODE_SIGNING_NAME`, `AZURE_CERT_PROFILE_NAME` (Azure Trusted Signing
through `trusted-signing-cli`); `TAURI_SIGNING_PRIVATE_KEY`,
`TAURI_SIGNING_PRIVATE_KEY_PASSWORD` (updater artifacts). Without them the
builds are unsigned and `createUpdaterArtifacts` stays off.

### How the Mac app is checked

| Step | Job | Passes when |
|---|---|---|
| `tauri build` | bundle | an `.app` and an unsigned arm64 DMG exist; the DMG is the `tessera-desktop-osx-arm64` artifact |
| launch the app | bundle | `Contents/MacOS/<exe>` with `TESSERA_DATA_DIR` set writes "core … ready on 127.0.0.1" to the shell log within 90 s; a screenshot is the `macos-app-launch` artifact |
| kill the shell | bundle | after `kill` (SIGTERM) on the shell, the core exits by itself within 20 s and `core.pid` is gone: no orphans |
| `dotnet test core` | smoke | every test passes |
| `dotnet test search` | smoke | every test passes; `TESSERA_MODELS_DIR` points at the downloaded model, so the embedder tests compare against the reference vectors, and CoreML against the CPU (`GpuMatchesCpu`, cosine ≥ 0.999) |
| model download | smoke | `ModelStore` fetches every file in the manifest from the public release, no token, and the sha256s match. The folder is cached with `actions/cache`, keyed on the manifest's sha256s |
| search | smoke | `tools/smoke search --require-gpu`: the CoreML cache is cleared first; with `PreferGpu` true the first search answers on the CPU within 30 s, then the device moves to `CoreML`; with true and false the red swatch beats green and blue for "a solid red square"; the vectors agree (cosine ≥ 0.999) between CoreML and the CPU and across the move; images per second and text milliseconds for both; a second launch moves to CoreML from the cache |
| the core | smoke | `tools/smoke core`: the published `osx-arm64` core started with the contract section 1 environment and `TESSERA_WEB_ORIGIN=tauri://localhost`. 24 checks: one ready line within 30 s and nothing else on stdout; `core.pid`; `/health/*` 200 with and without the token; `/api/library` 401 without or with a wrong token and 200 with `?sessionKey=`; CORS preflight 204 for `tauri://localhost`; `POST /api/library/roots` 201 on 50 generated PNGs and JPEGs plus one HEIC made with `sips`; `/api/search` answers within 60 s of the process starting; every picture scanned, thumbnailed (one `.webp` each) and embedded; `/api/search/status` says `device: coreml` within 20 minutes (`--expect-device coreml`); `/api/search` puts `red.png` first; stdin closed, exit 0 within 20 s, `core.pid` gone; the library folder unchanged, byte for byte |
| CoreML cache | smoke | not a gate. What the cache holds (sizes, weight files, how `model.mil` stores weights), and the vision model loaded from a copy without the uncompiled packages |
| Core ML alone | smoke | not a gate. `tools/smoke/coreml_load.swift` times Core ML loading the compiled vision model the search step cached, without ONNX Runtime |

Run the harness anywhere (`dotnet build src-tauri/tools/smoke -c Release -o <dir>`):

```bash
tessera-smoke images <dir> --count 50
tessera-smoke model  <models-dir>
tessera-smoke search <models-dir> --require-gpu
tessera-smoke core   <core-exe> <library-dir> <new-data-dir> --models-from <models-dir> --embeddings --expect-device coreml
tessera-smoke coreml <tower.onnx> [--batch 8] [--units CPUAndGPU] [--format MLProgram] [--cache <dir>] [--loads 2] [--option key=value] [--verbose]
```

`coreml` is how the CoreML options were chosen: one tower in a raw ONNX
Runtime session on CoreML, compared with the CPU picture by picture. With
`models/coreml_compat.py variants` (candidate graphs) it ran as a temporary
CI job in runs
[38947651278](https://github.com/arbaaz360/tessera-desktop/actions/runs/38947651278)
and [38953158750](https://github.com/arbaaz360/tessera-desktop/actions/runs/38953158750);
`models/README.md` and `search/README.md` have what they found.

### macOS numbers

Run [38872561745](https://github.com/arbaaz360/tessera-desktop/actions/runs/38872561745),
2026-10-16, `macos-latest` (macOS 26, Apple silicon, 3 cores).

| Measure | Value |
|---|---|
| core publish, `osx-arm64` | 354 files, 166 MB |
| DMG, unsigned | `Tessera Desktop_0.1.0_aarch64.dmg`, 64.8 MiB |
| `.app` | 174.3 MiB |
| `tauri build` | 3 min 26 s to compile the shell, 28 s for the DMG |
| app start to "core ready" in the shell log | 0.9 s |
| core start to ready line (smoke) | 806 ms |
| model download from the public release | 720 MB in 11.1 s, 65 MB/s |
| `dotnet test core` | 95 of 95 |
| `dotnet test search` | 37 of 38 then; 41 of 41 with model v3 and CoreML (below) |
| embedder against the PyTorch reference (CPU) | cosine 1.00000 for 256 px images and every sentence; 0.99677 and 0.99980 for resized inputs |
| search device with `PreferGpu` true | `Cpu` then: CoreML refused model v2. `CoreML` from model v3 (below) |
| CPU, images per second | 1.5 (batch 8, 640x480 inputs) |
| CPU, text per sentence | 126 to 135 ms (median of 10) |
| model load (sha256 check plus both sessions) | 6.0 to 6.8 s |
| "a solid red square" | red 0.1612, blue 0.1174, green 0.1028, on both settings |
| scan plus thumbnails, 51 pictures (one HEIC through `sips`) | 3.4 s, 15 per second, 0 failed |
| embeddings inside the core, 51 pictures | 48.7 s including a 4.8 s model load, about 1.2 per second (CPU) |
| `/api/search?q=a solid red square` | `red.png` first, score 0.160 |
| stdin closed to exit | 343 ms, exit code 0 |
| vector index, 200,000 vectors, top-100 search | 103.6 ms median |

### macOS with CoreML (manifest v4: v3's vision tower on macOS)

Runs [38968062311](https://github.com/arbaaz360/tessera-desktop/actions/runs/38968062311)
and [38971746054](https://github.com/arbaaz360/tessera-desktop/actions/runs/38971746054),
2026-10-16, same runner; ranges include runs 38953158750 and 38956260548.
The search library is ready on the CPU at once and moves to CoreML when it
has compiled (`search/README.md`).

| Measure | Value |
|---|---|
| ready on the CPU, `PreferGpu` true | 3.4 to 6.3 s (second launch 2.2 to 2.3 s) |
| first search answered, from the start of the load | 8.1 to 14.1 s, red first |
| moved to CoreML | 261 to 330 s after ready; 125 to 145 s on a second launch, from the cache |
| "a solid red square" on CoreML / CPU | red 0.1609 / 0.1612 first, then blue, then green |
| vectors, CoreML against CPU (red, green, blue, sentence) | 1.00000, 1.00000, 1.00000, 0.99996 |
| images per second (batch 8, 640x480), CoreML / CPU | 8.5 to 12.9 / 1.2 to 1.7 |
| text per sentence (median of 10), CoreML / CPU | 9.5 to 15.2 / 125 to 147 ms |
| Core ML alone loading the cached vision model (Swift) | 99 to 144 s, 48 to 56 s again, 79 to 97 s CPU-only |
| CoreML cache | 2,590 MB; the compiled models hold their weights as text; 2.0 GB without the uncompiled packages, which still loads |
| `dotnet test search` | 44 of 44; `GpuMatchesCpu` CoreML vs CPU: text 0.99986, image 1.00000 |
| model download, macOS entries of manifest v4, no token | 559 MB in 6 to 10 s |

Inside the published core (26 of 26 checks): ready line 2.1 s;
`/api/search` answers 8.1 to 9.1 s after the process started, on the CPU; 51
pictures scanned and thumbnailed in 2.4 s and all embedded 55 s later,
most on the CPU while CoreML compiled; `/api/search/status` says
`device: coreml` 209 to 274 s after start; `red.png` first at 0.160 with the index
built on both devices; stdin closed to exit 368 ms.

## Not done

- **A drag by hand from File Explorer** was not made here: desktop control
  of File Explorer was declined for this session. The path from Tauri's
  drag-drop events onward was run in a real window (above); the step before
  it, Windows' OLE drop turned into those events by wry, and the shell's
  `drop:` log line, have not run on this machine.
- **Add pictures in a window** opens the system dialog; it was built and
  type-checked, not clicked here (no desktop control).
- **A drag from a browser** in the Tauri window: see `web/README.md`.
- Certificates and the updater key do not exist; `plugins.updater.pubkey`
  and the endpoint are placeholders and the update check is skipped.
- The Gumroad product id is a placeholder, so every key is `invalid` until
  `TESSERA_GUMROAD_PRODUCT_ID` is set at build time.
- The 14-day grace period after a revocation (Memories Rescue SRS 9.3 rule 4)
  is not implemented: a revoked key reverts to `free` at the next re-check.
- The licence cache is not HMAC-protected (SRS 9.3 rule 5).
- No deactivate command (SRS FR-47).
- **CoreML on a real Mac is unmeasured.** The runner's Core ML has no GPU
  or Neural Engine for ML (every op on its CPU backend). There search
  answers on the CPU within seconds, and CoreML takes over 261 s after the
  first launch and 125 s after later ones, from a 2.6 GB cache
  (`search/README.md`, Not done).
- The DMG is unsigned. A Mac that downloads it quarantines it, and Gatekeeper
  refuses an unsigned app; testers can clear the flag with
  `xattr -dr com.apple.quarantine "/Applications/Tessera Desktop.app"`.
- When signing exists: Tauri signs the app's own binary, not the 354 files
  in `Resources/resources/core`. Notarization needs every Mach-O in there
  signed with the hardened runtime and `entitlements.plist` (JIT), so CI
  will have to `codesign` the core's files before `tauri build`.
- The window is 1280 points wide and the runner's screen 1024, so the
  launch screenshot shows it cut off on the left. Real Macs are wider.
- ~~GitHub Actions stopped starting jobs on 2026-10-16 (billing).~~ The
  repository is public since 2026-10-16 and Actions runs again.
