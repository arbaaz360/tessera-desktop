# Builds the stub core and puts it where the shell looks for the real one:
# src-tauri\resources\core\tessera-core.exe. Run from anywhere.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcTauri = Split-Path -Parent $here
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"

cargo build --release --manifest-path "$here\stub-core\Cargo.toml"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force "$srcTauri\resources\core" | Out-Null
Copy-Item "$here\stub-core\target\release\stub-core.exe" "$srcTauri\resources\core\tessera-core.exe" -Force
Write-Host "stub core installed at $srcTauri\resources\core\tessera-core.exe"
