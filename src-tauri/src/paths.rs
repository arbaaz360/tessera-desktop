//! Where things live: the data dir (docs/CONTRACT.md section 7), the logs, the
//! core executable inside the bundled resources.

use std::path::{Path, PathBuf};
use tauri::{AppHandle, Manager, Runtime};

/// The folder name under the per-user local data dir.
pub const APP_DIR_NAME: &str = "TesseraDesktop";

/// The core's file name, from the contract's packaging table (section 8).
pub const CORE_EXE: &str = if cfg!(windows) {
    "tessera-core.exe"
} else {
    "tessera-core"
};

/// The one origin the core lets through CORS (contract section 1).
pub const WEB_ORIGIN: &str = if cfg!(windows) {
    "http://tauri.localhost"
} else {
    "tauri://localhost"
};

/// `%LOCALAPPDATA%\TesseraDesktop` on Windows,
/// `~/Library/Application Support/TesseraDesktop` on macOS.
/// `TESSERA_DATA_DIR` in the shell's own environment overrides it, which is
/// how a test run keeps its own scratch data dir.
pub fn data_dir<R: Runtime>(app: &AppHandle<R>) -> Result<PathBuf, String> {
    if let Some(v) = std::env::var_os("TESSERA_DATA_DIR")
        && !v.is_empty()
    {
        return Ok(PathBuf::from(v));
    }
    let base = app
        .path()
        .local_data_dir()
        .map_err(|e| format!("no local data dir: {e}"))?;
    Ok(base.join(APP_DIR_NAME))
}

pub fn logs_dir(data: &Path) -> PathBuf {
    data.join("logs")
}

pub fn pid_file(data: &Path) -> PathBuf {
    data.join("core.pid")
}

pub fn licence_file(data: &Path) -> PathBuf {
    data.join("licence.json")
}

/// `resources/core/tessera-core(.exe)` under the app's resource dir. In
/// `tauri dev` the resource dir is the cargo target dir, where tauri-build
/// copies the same files.
pub fn core_exe<R: Runtime>(app: &AppHandle<R>) -> Result<PathBuf, String> {
    let res = app
        .path()
        .resource_dir()
        .map_err(|e| format!("no resource dir: {e}"))?;
    Ok(res.join("resources").join("core").join(CORE_EXE))
}
