#!/usr/bin/env bash
# Builds the stub core and puts it where the shell looks for the real one:
# src-tauri/resources/core/tessera-core(.exe). Run from anywhere.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
src_tauri="$(dirname "$here")"
export PATH="$HOME/.cargo/bin:$PATH"

cargo build --release --manifest-path "$here/stub-core/Cargo.toml"

ext=""
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) ext=".exe" ;; esac
mkdir -p "$src_tauri/resources/core"
cp "$here/stub-core/target/release/stub-core$ext" "$src_tauri/resources/core/tessera-core$ext"
echo "stub core installed at $src_tauri/resources/core/tessera-core$ext"
