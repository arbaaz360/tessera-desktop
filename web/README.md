# tessera-web

The web app of the photo library — https://tessera.samurai.local. It talks
**only to Tessera** (`HomeLab/Tessera`, :2500), which serves this build at `/`
so every `/api` call is same-origin.

## Build and deploy

```powershell
.\deploy.ps1
```

Type-checks, builds with Vite, and copies `dist/` to `TESSERA_WEB_ROOT`
(declared once in `HomeLab/homelab.env`, currently `C:/tessera-web`), which
tessera-api mounts read-only. index.html is served no-cache, so the new
bundle is live on the next page load — no restart.

```powershell
pnpm test        # vitest
pnpm typecheck
```

## What is where

| Folder | Does |
|---|---|
| `features/timeline` | the photo grid, justified layout, buckets by month |
| `features/albums` | albums list and detail; move, merge, overlap; **Index** (scan the folder, faces to FaceSearch, images to Foundation) |
| `features/viewer` | full-screen viewer: thumbhash → grid thumb → 1440px preview → the original |
| `features/search` | search by words, ranked by Foundation through Tessera; kind and date filters; a file-name mode; album-scoped when opened from an album |
| `features/faces` | who is in a picture (FaceSearch through Tessera) |
| `features/trash` | hidden pictures; restore only - nothing is ever deleted |
| `features/music` | songs identified in videos |
| `features/covers` | pick an album cover from ranked candidates |
| `features/admin` | jobs page (queues, folders waiting for a scan, workers) and admin (accounts and roots, health counts, duplicate sets) |
| `features/auth` | sessions; several accounts at once, one active |
| `shared/api` | the HTTP layer: `tessera.ts` (JSON), `client.ts` (image and video URLs) |

## Rules that shaped it

- The app never calls FaceSearch, Foundation or a worker directly; Tessera
  does, and answers in its own ids (`HomeLab/CLAUDE.md` §3).
- Heavy work is queued here and started by a person (the jobs page, the Index
  button); nothing starts on a timer from the browser.
- `<img>` URLs carry the active account's session key because images cannot
  send headers — see `shared/api/client.ts`.
