//! One log for the shell and the core's stderr:
//! `<data>/logs/shell-YYYY-MM-DD.log`, one line per event, UTC. In debug
//! builds every line also goes to the shell's own stderr.

use log::{Level, LevelFilter, Log, Metadata, Record};
use std::{
    fs::{self, File, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
    sync::Mutex,
};

struct FileLogger {
    dir: PathBuf,
    file: Mutex<Option<(String, File)>>,
    to_stderr: bool,
}

fn today() -> String {
    chrono::Utc::now().format("%Y-%m-%d").to_string()
}

pub fn file_for(logs_dir: &Path, day: &str) -> PathBuf {
    logs_dir.join(format!("shell-{day}.log"))
}

/// Today's log file. The error page shows this path.
pub fn current_file(logs_dir: &Path) -> PathBuf {
    file_for(logs_dir, &today())
}

impl FileLogger {
    fn write_line(&self, line: &str) {
        let day = today();
        let mut guard = self.file.lock().unwrap_or_else(|p| p.into_inner());
        let stale = guard.as_ref().map(|(d, _)| d != &day).unwrap_or(true);
        if stale {
            let _ = fs::create_dir_all(&self.dir);
            *guard = OpenOptions::new()
                .create(true)
                .append(true)
                .open(file_for(&self.dir, &day))
                .ok()
                .map(|f| (day, f));
        }
        if let Some((_, f)) = guard.as_mut() {
            let _ = writeln!(f, "{line}");
        }
        if self.to_stderr {
            eprintln!("{line}");
        }
    }
}

impl Log for FileLogger {
    /// Everything from this crate and the core; from dependencies, info and up.
    fn enabled(&self, m: &Metadata) -> bool {
        let ours = m.target().starts_with("tessera_desktop") || m.target() == "core";
        m.level() <= if ours { Level::Debug } else { Level::Info }
    }

    fn log(&self, r: &Record) {
        if !self.enabled(r.metadata()) {
            return;
        }
        let ts = chrono::Utc::now().format("%Y-%m-%dT%H:%M:%S%.3fZ");
        self.write_line(&format!(
            "{ts} {:<5} {} {}",
            r.level(),
            r.target(),
            r.args()
        ));
    }

    fn flush(&self) {}
}

/// Installs the logger. Returns the logs dir. Safe to call once; a second
/// call keeps the first logger.
pub fn init(data_dir: &Path) -> PathBuf {
    let dir = crate::paths::logs_dir(data_dir);
    let logger = FileLogger {
        dir: dir.clone(),
        file: Mutex::new(None),
        to_stderr: cfg!(debug_assertions),
    };
    if log::set_boxed_logger(Box::new(logger)).is_ok() {
        log::set_max_level(LevelFilter::Debug);
    }
    dir
}
