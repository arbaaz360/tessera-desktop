# Tessera Desktop — rules for any session working here

Read `docs/CONTRACT.md` before anything else. It is the only agreement between
the four parts of this app, and it is frozen for the part sessions: if your
work needs it changed, tell the coordinating session and carry on with what
does not depend on the change.

## Boundaries

| Part | Owns | Touches nothing else |
|---|---|---|
| core | `core/` | |
| search | `search/`, `models/` | |
| web | `web/` | |
| shell | `src-tauri/`, `.github/` | |

The coordinator owns the root files, `docs/`, `fixtures/` and the
integration. Two sessions never edit the same file. If you need a file
outside your folder, ask.

## How to work

- Commit early and often on your branch and push it. Never force-push. Never
  rewrite another branch.
- Tests run per part: `dotnet test core`, `dotnet test search`,
  `pnpm --dir web test` and `pnpm --dir web typecheck`, `cargo test` in
  `src-tauri`. A part is done when its tests pass and its README says how to
  run it.
- Each part keeps a short `README.md` in its folder: what it is, how to build,
  how to run, what is not done. Plain words, a table over a paragraph, no
  version numbers in prose.
- Report numbers, not adjectives: images per second, milliseconds per page,
  megabytes of RAM.

## Product rules (from the plan, not negotiable)

- Never write inside a library root. Never delete a user's file. Trash is a
  mark.
- No network except the model download, Gumroad licence verification and
  the update feed. Nothing phones home.
- A file's identity is its sha256; the path is an attribute. Thumbnail paths
  are derived from the hash, never stored.
- Status answers carry counts. A drift of 3 and a drift of 300,000 must not
  look the same.
- No faces, no people, no "who is this" in v1.

## This machine

| Tool | Where |
|---|---|
| .NET SDK 10 | `dotnet` on PATH |
| Node 24, pnpm 12 | on PATH |
| Rust (cargo 1.97, MSVC target) | `~/.cargo/bin` — add it to PATH in Git Bash: `export PATH="$HOME/.cargo/bin:$PATH"` |
| Tauri CLI | `pnpm dlx @tauri-apps/cli@2` (Memories Rescue, `C:\Users\ASUS\Projects\Adhoc\MemoriesRescue`, uses the same) |
| Python 3.12 | `python`; make a venv for model export |
| GPU | RTX 5060 Ti. It belongs to Inkwell first; keep benchmarks short and never leave a job running |

The HomeLab originals this was forked from: `C:\Users\ASUS\Projects\HomeLab\Tessera`
and `C:\Users\ASUS\Projects\HomeLab\tessera-web`. Read them for intent; do not
edit them.
