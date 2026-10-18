//! Tessera Desktop shell. Owns the window, starts and supervises the core,
//! exposes the contract's commands (docs/CONTRACT.md section 5) to the web app.

pub mod applog;
pub mod commands;
pub mod core;
pub mod import;
pub mod licence;
pub mod paths;
pub mod updater;

use crate::core::{CoreConfig, CoreInfo, Hooks, Supervisor};
use std::path::{Path, PathBuf};
use tauri::{AppHandle, Emitter, Manager, Runtime};

/// Managed state: where the shell's log files live.
pub struct LogsDir(pub PathBuf);

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        // First plugin, so a second launch is caught before anything else runs.
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            if let Some(w) = app.get_webview_window("main") {
                let _ = w.unminimize();
                let _ = w.show();
                let _ = w.set_focus();
            }
        }))
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .invoke_handler(tauri::generate_handler![
            commands::core_info,
            commands::pick_folder,
            commands::pick_files,
            commands::open_external,
            commands::app_version,
            commands::licence_get,
            commands::licence_activate,
            commands::core_restart,
            commands::open_logs,
        ])
        // Drops reach the web app as `tauri://drag-*` events with their paths;
        // it sends them to the core. Logged here in counts (src/import.rs).
        .on_window_event(|_window, event| {
            if let tauri::WindowEvent::DragDrop(tauri::DragDropEvent::Drop { paths, .. }) = event {
                let s = import::summarize(paths);
                log::info!(
                    "drop: {} paths: {} files ({} pictures or videos), {} folders, {} not found",
                    paths.len(),
                    s.files,
                    s.media,
                    s.folders,
                    s.missing
                );
            }
        })
        .setup(|app| setup(app.handle().clone()))
        .build(tauri::generate_context!())
        .expect("error while building tauri application")
        .run(|app, event| {
            if let tauri::RunEvent::Exit = event {
                log::info!("app exit");
                app.state::<Supervisor>().stop();
            }
        });
}

fn setup<R: Runtime>(app: AppHandle<R>) -> Result<(), Box<dyn std::error::Error>> {
    let data_dir = paths::data_dir(&app)?;
    std::fs::create_dir_all(&data_dir)?;
    let logs_dir = applog::init(&data_dir);
    log::info!(
        "Tessera Desktop {} starting; data dir {}",
        app.package_info().version,
        data_dir.display()
    );
    app.manage(LogsDir(logs_dir.clone()));

    let exe = paths::core_exe(&app)?;
    let cfg = CoreConfig {
        exe,
        data_dir: data_dir.clone(),
        web_origin: paths::WEB_ORIGIN.into(),
        dev: cfg!(debug_assertions),
    };

    app.manage(licence::Licence::new(&data_dir));

    let ready_app = app.clone();
    let fatal_app = app.clone();
    let hooks = Hooks {
        on_ready: Box::new(move |info: &CoreInfo| {
            let app = ready_app.clone();
            let info = info.clone();
            tauri::async_runtime::spawn(async move {
                app.state::<licence::Licence>().sync_core(&info).await;
                let _ = app.emit("core-ready", &info);
            });
        }),
        on_fatal: Box::new(move |message: &str| {
            show_core_error(&fatal_app, message, &applog::current_file(&logs_dir));
        }),
    };
    let supervisor = Supervisor::new(cfg, hooks);
    supervisor.start();
    app.manage(supervisor);

    updater::check_at_launch(app.clone());
    Ok(())
}

/// The core is gone for good: tell the web app, and replace the page with a
/// plain notice that names the log file and offers a restart.
fn show_core_error<R: Runtime>(app: &AppHandle<R>, message: &str, log_file: &Path) {
    let log_path = log_file.to_string_lossy().into_owned();
    let _ = app.emit(
        "core-error",
        serde_json::json!({ "message": message, "logPath": log_path }),
    );
    if let Some(w) = app.get_webview_window("main") {
        let js = include_str!("error-page.js")
            .replace(
                "__MESSAGE__",
                &serde_json::to_string(message).unwrap_or_default(),
            )
            .replace(
                "__LOG_PATH__",
                &serde_json::to_string(&log_path).unwrap_or_default(),
            );
        let _ = w.eval(js);
    }
}
