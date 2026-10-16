# Tessera Desktop

A Windows and macOS app that indexes your own image folders in place, never
writes to them, finds pictures by describing them (SigLIP2, on your machine,
offline), shows duplicates, and keeps albums that survive moving files.

Working name. Forked 2026-10-16 from the HomeLab photo library
(`HomeLab/Tessera`, `HomeLab/tessera-web`). The plan is `docs/PLAN.md`; the
agreement between the parts is `docs/CONTRACT.md`.

## Layout

| Folder | Part | What |
|---|---|---|
| `core/` | core | `tessera-core`: .NET 10, Kestrel on loopback, SQLite; scanner, watch, thumbnails, embeddings, the `/api` |
| `search/` | search | `Tessera.Desktop.Search`: SigLIP2 through ONNX Runtime, the vector index; `Abstractions` is the frozen interface |
| `models/` | search | export scripts and the model manifest |
| `web/` | web | the React app (Vite, TanStack, Tailwind) |
| `src-tauri/` | shell | the Tauri 2 app: window, sidecar lifecycle, folder dialog, keychain, licence, updater |
| `fixtures/` | coordinator | generator for a large synthetic library |
| `docs/` | coordinator | plan, contract, decisions |

## Status, 2026-10-16

All four parts are merged on `main`. The Windows release build was run end to
end on the owner's PC: installer build, launch, onboarding, folder added
through the core, scan, thumbnails, SigLIP2 embeddings on DirectML, text
search, "more like this", file-name search, duplicates, folder removal and a
clean shutdown.

| Measured (Windows, RTX 5060 Ti, NVMe) | Number |
|---|---|
| MSI / NSIS installer | 75 MB / 55 MB, without the model |
| Model download, first run, no token, from the public model repo | 755 MB in about 2 min, hashes verified |
| 300 screenshots, folder added → searchable | 11.5 s, 0 errors |
| Text search over 336 pictures | about 30 ms |
| "More like this" | 4 ms |
| Core shutdown on window close | exit 0 in 0.3 s, no orphan |

Model files are hosted in the public, model-only repository
[`arbaaz360/siglip2-base-onnx`](https://github.com/arbaaz360/siglip2-base-onnx)
(owner's decision, 2026-10-16); this repository stays private. A fresh
install with no token downloaded, verified and loaded the model, then
searched correctly.

Search quality: on photographs it ranks as a person would. Nine queries over
the Windows wallpapers and colour swatches all put the right pictures first
(lakes, pines, cherry blossom, fabric, each colour). On screenshots of
application windows it is weak, as the search README already found; that is
the model's domain, not a defect, and the buyers keep photographs and
artwork.

| Not done | Why |
|---|---|
| macOS | nothing has run on a Mac yet |
| Signing, updater key, Gumroad product id | placeholders until the certificates and the product exist |
| Duplicate copies counted as "moved" in a scan's numbers | small counting defect in the scanner; the duplicates view itself is right |
| Smaller model download | the text tower's vocabulary is 565 MB; int8 on that table would save about 200 MB |

## Build

```bash
dotnet test core
dotnet test search
pnpm --dir web install && pnpm --dir web test && pnpm --dir web build
dotnet publish core/src/Tessera.Api -c Release -r win-x64 --self-contained -o src-tauri/resources/core
cd src-tauri && pnpm dlx @tauri-apps/cli@2 build
```

Each part's own `README.md` says more.
