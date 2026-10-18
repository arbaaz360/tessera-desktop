//! The commands the web app invokes (docs/CONTRACT.md section 5), plus two
//! the shell's own error page uses.

use crate::{
    core::{CoreInfo, READY_TIMEOUT, Supervisor},
    licence::{ActivateAnswer, Answer, Licence},
};
use std::time::Duration;
use tauri::{AppHandle, Manager, Runtime, State};
use tauri_plugin_dialog::DialogExt;
use tauri_plugin_opener::OpenerExt;

/// `{"baseUrl": "http://127.0.0.1:N", "token": "…"}`. Waits for the core when
/// it is still starting; errors with the reason when the shell gave up.
#[tauri::command]
pub async fn core_info(sup: State<'_, Supervisor>) -> Result<CoreInfo, String> {
    sup.wait_ready(READY_TIMEOUT + Duration::from_secs(5)).await
}

/// Absolute paths of the pictures and videos the user picked (several at
/// once), or `null` when they cancelled. The web app sends them to the core's
/// `POST /api/import`, which indexes what is inside a library folder where it
/// is and copies the rest into the Tessera folder.
#[tauri::command]
pub async fn pick_files<R: Runtime>(app: AppHandle<R>) -> Result<Option<Vec<String>>, String> {
    let picked = tauri::async_runtime::spawn_blocking(move || {
        app.dialog()
            .file()
            .set_title("Add pictures")
            .add_filter("Pictures and videos", crate::import::MEDIA_EXTENSIONS)
            .blocking_pick_files()
    })
    .await
    .map_err(|e| format!("dialog: {e}"))?;
    match picked {
        None => Ok(None),
        Some(files) => files
            .into_iter()
            .map(|f| {
                f.simplified()
                    .into_path()
                    .map(|p| p.to_string_lossy().into_owned())
                    .map_err(|e| format!("dialog: {e}"))
            })
            .collect::<Result<Vec<_>, _>>()
            .map(|paths| if paths.is_empty() { None } else { Some(paths) }),
    }
}

/// An absolute path, or `null` when the user cancelled.
#[tauri::command]
pub async fn pick_folder<R: Runtime>(app: AppHandle<R>) -> Result<Option<String>, String> {
    let picked = tauri::async_runtime::spawn_blocking(move || {
        app.dialog()
            .file()
            .set_title("Choose a folder to index")
            .blocking_pick_folder()
    })
    .await
    .map_err(|e| format!("dialog: {e}"))?;
    match picked {
        None => Ok(None),
        Some(p) => p
            .simplified()
            .into_path()
            .map(|p| Some(p.to_string_lossy().into_owned()))
            .map_err(|e| format!("dialog: {e}")),
    }
}

/// Opens `http`, `https` and `mailto` URLs in the system browser; nothing else.
#[tauri::command]
pub fn open_external<R: Runtime>(app: AppHandle<R>, url: String) -> Result<(), String> {
    let lower = url.trim().to_ascii_lowercase();
    if !(lower.starts_with("https://")
        || lower.starts_with("http://")
        || lower.starts_with("mailto:"))
    {
        return Err("only http, https and mailto URLs can be opened".into());
    }
    app.opener()
        .open_url(url.trim(), None::<&str>)
        .map_err(|e| format!("open: {e}"))
}

#[tauri::command]
pub fn app_version<R: Runtime>(app: AppHandle<R>) -> String {
    app.package_info().version.to_string()
}

#[tauri::command]
pub fn licence_get(lic: State<'_, Licence>) -> Answer {
    lic.get()
}

#[tauri::command]
pub async fn licence_activate(
    key: String,
    lic: State<'_, Licence>,
    sup: State<'_, Supervisor>,
) -> Result<ActivateAnswer, String> {
    Ok(lic.activate(&key, sup.info().as_ref()).await)
}

/// Shell-internal (the error page's "Restart engine" button): stops the core
/// and starts it again with a fresh restart budget.
#[tauri::command]
pub async fn core_restart(sup: State<'_, Supervisor>) -> Result<CoreInfo, String> {
    let s = sup.inner().clone();
    tauri::async_runtime::spawn_blocking(move || s.restart())
        .await
        .map_err(|e| format!("restart: {e}"))?;
    sup.wait_ready(READY_TIMEOUT + Duration::from_secs(5)).await
}

/// Shell-internal (the error page's "Open log folder" button).
#[tauri::command]
pub fn open_logs<R: Runtime>(app: AppHandle<R>) -> Result<(), String> {
    let logs = app.state::<crate::LogsDir>().0.clone();
    app.opener()
        .open_path(logs.to_string_lossy().into_owned(), None::<&str>)
        .map_err(|e| format!("open: {e}"))
}
