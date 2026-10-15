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

## Build

```bash
dotnet test core
dotnet test search
pnpm --dir web install && pnpm --dir web test && pnpm --dir web build
dotnet publish core/src/Tessera.Api -c Release -r win-x64 --self-contained -o src-tauri/resources/core
cd src-tauri && pnpm dlx @tauri-apps/cli@2 build
```

Each part's own `README.md` says more.
