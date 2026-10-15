//! The updater plugin is registered with a placeholder public key until the
//! signing keys exist. Until then nothing is checked; with a real key the
//! shell checks once at launch, logs the answer and tells the web app. It
//! never installs on its own.

use tauri::{AppHandle, Emitter, Runtime};
use tauri_plugin_updater::UpdaterExt;

pub const PUBKEY_PLACEHOLDER_PREFIX: &str = "PLACEHOLDER";

/// The configured public key, when it is a real one.
pub fn configured_pubkey<R: Runtime>(app: &AppHandle<R>) -> Option<String> {
    let key = app
        .config()
        .plugins
        .0
        .get("updater")?
        .get("pubkey")?
        .as_str()?
        .trim()
        .to_string();
    if key.is_empty() || key.starts_with(PUBKEY_PLACEHOLDER_PREFIX) {
        None
    } else {
        Some(key)
    }
}

pub fn check_at_launch<R: Runtime>(app: AppHandle<R>) {
    if configured_pubkey(&app).is_none() {
        log::info!("updater: inactive, no public key configured");
        return;
    }
    tauri::async_runtime::spawn(async move {
        let updater = match app.updater() {
            Ok(u) => u,
            Err(e) => {
                log::warn!("updater: {e}");
                return;
            }
        };
        match updater.check().await {
            Ok(Some(update)) => {
                log::info!("updater: version {} is available", update.version);
                let _ = app.emit(
                    "update-available",
                    serde_json::json!({ "version": update.version }),
                );
            }
            Ok(None) => log::info!("updater: up to date"),
            Err(e) => log::warn!("updater: check failed: {e}"),
        }
    });
}
