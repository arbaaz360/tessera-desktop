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
| `src/error-page.js` | what the window shows when the shell gives up on the core |
| `tauri.conf.json` | the app config from the contract |
| `tauri.stub.conf.json` | merged with `--config`: static dev page instead of the web app |
| `capabilities/default.json` | `core:default` only; no plugin is exposed to JavaScript |
| `tools/stub-core/` | a stand-in core for testing the shell on its own |
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

`.github/workflows/build.yml`, two jobs on `windows-latest` and `macos-latest`:

| Job | Does |
|---|---|
| `shell` | stub core, `cargo fmt --check`, `clippy -D warnings`, `cargo test` |
| `bundle` | .NET 10 publish of the core, pnpm web build, `tauri build`, installers as artifacts |

Signing steps exist and run only when their secrets exist: `APPLE_CERTIFICATE`,
`APPLE_CERTIFICATE_PASSWORD`, `APPLE_SIGNING_IDENTITY`, `APPLE_ID`,
`APPLE_PASSWORD`, `APPLE_TEAM_ID` (Tauri's own names); `AZURE_CLIENT_ID`,
`AZURE_CLIENT_SECRET`, `AZURE_TENANT_ID`, `AZURE_ENDPOINT`,
`AZURE_CODE_SIGNING_NAME`, `AZURE_CERT_PROFILE_NAME` (Azure Trusted Signing
through `trusted-signing-cli`); `TAURI_SIGNING_PRIVATE_KEY`,
`TAURI_SIGNING_PRIVATE_KEY_PASSWORD` (updater artifacts). Without them the
builds are unsigned and `createUpdaterArtifacts` stays off.

## Not done

- Certificates and the updater key do not exist; `plugins.updater.pubkey`
  and the endpoint are placeholders and the update check is skipped.
- The Gumroad product id is a placeholder, so every key is `invalid` until
  `TESSERA_GUMROAD_PRODUCT_ID` is set at build time.
- The 14-day grace period after a revocation (Memories Rescue SRS 9.3 rule 4)
  is not implemented: a revoked key reverts to `free` at the next re-check.
- The licence cache is not HMAC-protected (SRS 9.3 rule 5).
- No deactivate command (SRS FR-47).
- Not tried on macOS yet; the macOS paths and the entitlements come from the
  plan, not from a run.
