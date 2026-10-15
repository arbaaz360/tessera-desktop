//! Core lifecycle (docs/CONTRACT.md section 1): find the executable, kill a
//! stale core named in `core.pid`, spawn with the contract's environment, wait
//! for the ready line, keep stdin open, supervise, restart, stop.
//!
//! This module knows nothing about Tauri. `lib.rs` wires its hooks to the
//! window.

use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use serde::{Deserialize, Serialize};
use std::{
    collections::VecDeque,
    io::{BufRead, BufReader},
    path::{Path, PathBuf},
    process::{Child, ChildStdin, Command, ExitStatus, Stdio},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc,
    },
    thread,
    time::{Duration, Instant},
};
use tokio::sync::watch;

/// How long the core gets to print its ready line.
pub const READY_TIMEOUT: Duration = Duration::from_secs(30);
/// How long the core gets to leave after stdin closes before it is killed.
pub const STOP_GRACE: Duration = Duration::from_secs(5);
/// Unexpected exits tolerated per [`RESTART_WINDOW`] before the shell gives up.
pub const RESTART_LIMIT: usize = 3;
pub const RESTART_WINDOW: Duration = Duration::from_secs(60);

const POLL: Duration = Duration::from_millis(100);

// ---------------------------------------------------------------- ready line

/// What `core_info()` answers (contract section 5).
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct CoreInfo {
    pub base_url: String,
    pub token: String,
}

/// The one JSON line the core prints on stdout.
#[derive(Clone, Debug, Deserialize, PartialEq, Eq)]
pub struct Ready {
    pub port: u16,
    pub pid: u32,
    pub version: String,
}

/// Parses one stdout line. `{"event":"ready","port":N,"pid":P,"version":"…"}`
/// is the ready line; anything else (another event, a log line that leaked,
/// garbage) is `None`.
pub fn parse_ready_line(line: &str) -> Option<Ready> {
    #[derive(Deserialize)]
    struct Line {
        event: String,
        port: u16,
        pid: u32,
        version: String,
    }
    let v: Line = serde_json::from_str(line.trim()).ok()?;
    if v.event != "ready" || v.port == 0 {
        return None;
    }
    Some(Ready {
        port: v.port,
        pid: v.pid,
        version: v.version,
    })
}

/// 32 random bytes as base64url without padding: 43 characters.
pub fn new_token() -> String {
    let mut buf = [0u8; 32];
    getrandom::fill(&mut buf).expect("the OS random source is unavailable");
    URL_SAFE_NO_PAD.encode(buf)
}

// ---------------------------------------------------------------- stale pid

/// What a process table says about one pid.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Lookup {
    /// No such process.
    Dead,
    /// A process exists; its executable path when the OS lets us read it.
    Alive(Option<PathBuf>),
}

/// The two questions the stale-pid check asks the OS. The real answer comes
/// from `sysinfo`; the unit tests use a fake table.
pub trait ProcessTable {
    fn lookup(&mut self, pid: u32) -> Lookup;
    /// Kills the process and waits until it is gone. `true` when it is gone.
    fn kill(&mut self, pid: u32) -> bool;
}

#[derive(Debug, PartialEq, Eq)]
pub enum StaleOutcome {
    NoPidFile,
    /// The file held no pid; it was removed.
    Unreadable,
    /// The pid is not running; the file was removed.
    NotRunning(u32),
    /// The pid was reused by something that is not our core; the file was removed.
    NotOurs {
        pid: u32,
        exe: Option<PathBuf>,
    },
    /// A core from an earlier run was killed; the file was removed.
    Killed(u32),
    /// Our core is alive and would not die. The file stays.
    KillFailed(u32),
}

fn file_name_lower(p: &Path) -> Option<String> {
    p.file_name().map(|n| n.to_string_lossy().to_lowercase())
}

/// "Ours" means the same executable name as the core we are about to start.
/// The path may differ: an update moves the install dir, and the stale core
/// belongs to the old one.
pub fn is_our_core(exe: &Path, our_exe: &Path) -> bool {
    match (file_name_lower(exe), file_name_lower(our_exe)) {
        (Some(a), Some(b)) => a == b,
        _ => false,
    }
}

/// Reads `core.pid`; if it names a live process whose executable is ours,
/// kills it. Removes the file whenever it is known to be stale.
pub fn kill_stale(pid_file: &Path, our_exe: &Path, table: &mut dyn ProcessTable) -> StaleOutcome {
    let text = match std::fs::read_to_string(pid_file) {
        Ok(t) => t,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return StaleOutcome::NoPidFile,
        Err(_) => return StaleOutcome::Unreadable,
    };
    let Ok(pid) = text.trim().parse::<u32>() else {
        let _ = std::fs::remove_file(pid_file);
        return StaleOutcome::Unreadable;
    };
    if pid == std::process::id() {
        let _ = std::fs::remove_file(pid_file);
        return StaleOutcome::Unreadable;
    }
    let outcome = match table.lookup(pid) {
        Lookup::Dead => StaleOutcome::NotRunning(pid),
        Lookup::Alive(Some(exe)) if is_our_core(&exe, our_exe) => {
            if table.kill(pid) {
                StaleOutcome::Killed(pid)
            } else {
                StaleOutcome::KillFailed(pid)
            }
        }
        Lookup::Alive(exe) => StaleOutcome::NotOurs { pid, exe },
    };
    if !matches!(outcome, StaleOutcome::KillFailed(_)) {
        let _ = std::fs::remove_file(pid_file);
    }
    outcome
}

/// The real process table.
pub struct SysTable(sysinfo::System);

impl Default for SysTable {
    fn default() -> Self {
        Self::new()
    }
}

impl SysTable {
    pub fn new() -> Self {
        Self(sysinfo::System::new())
    }

    fn refresh(&mut self, pid: sysinfo::Pid) {
        use sysinfo::{ProcessRefreshKind, ProcessesToUpdate, UpdateKind};
        self.0.refresh_processes_specifics(
            ProcessesToUpdate::Some(&[pid]),
            true,
            ProcessRefreshKind::nothing().with_exe(UpdateKind::Always),
        );
    }
}

impl ProcessTable for SysTable {
    fn lookup(&mut self, pid: u32) -> Lookup {
        let pid = sysinfo::Pid::from_u32(pid);
        self.refresh(pid);
        match self.0.process(pid) {
            None => Lookup::Dead,
            Some(p) => Lookup::Alive(p.exe().map(Path::to_path_buf)),
        }
    }

    fn kill(&mut self, pid: u32) -> bool {
        let spid = sysinfo::Pid::from_u32(pid);
        let Some(p) = self.0.process(spid) else {
            return true;
        };
        if !p.kill() {
            return false;
        }
        let deadline = Instant::now() + STOP_GRACE;
        while Instant::now() < deadline {
            thread::sleep(POLL);
            self.refresh(spid);
            if self.0.process(spid).is_none() {
                return true;
            }
        }
        false
    }
}

// ---------------------------------------------------------------- supervisor

#[derive(Clone, Debug)]
pub struct CoreConfig {
    pub exe: PathBuf,
    pub data_dir: PathBuf,
    pub web_origin: String,
    /// `TESSERA_DEV=1`: the core also allows the Vite origin.
    pub dev: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Status {
    Starting,
    Ready(CoreInfo),
    /// The shell gave up. The message is for the error page.
    Failed(String),
    Stopped,
}

/// What the supervisor tells the rest of the app.
pub struct Hooks {
    /// A core is up (first start and every restart).
    pub on_ready: Box<dyn Fn(&CoreInfo) + Send + Sync>,
    /// The shell gave up on the core.
    pub on_fatal: Box<dyn Fn(&str) + Send + Sync>,
}

struct Running {
    child: Child,
    /// Held open for the core's lifetime; dropping it is the stop signal.
    stdin: Option<ChildStdin>,
    pid: u32,
}

struct Inner {
    cfg: CoreConfig,
    hooks: Hooks,
    status_tx: watch::Sender<Status>,
    status_rx: watch::Receiver<Status>,
    running: Mutex<Option<Running>>,
    stopping: AtomicBool,
    /// Bumped by `restart()` so an old supervisor thread steps aside.
    generation: AtomicU64,
    restarts: Mutex<VecDeque<Instant>>,
}

#[derive(Clone)]
pub struct Supervisor {
    inner: Arc<Inner>,
}

impl Supervisor {
    pub fn new(cfg: CoreConfig, hooks: Hooks) -> Self {
        let (status_tx, status_rx) = watch::channel(Status::Starting);
        Self {
            inner: Arc::new(Inner {
                cfg,
                hooks,
                status_tx,
                status_rx,
                running: Mutex::new(None),
                stopping: AtomicBool::new(false),
                generation: AtomicU64::new(0),
                restarts: Mutex::new(VecDeque::new()),
            }),
        }
    }

    /// Starts the supervisor thread. Returns at once.
    pub fn start(&self) {
        let inner = self.inner.clone();
        let generation = inner.generation.load(Ordering::SeqCst);
        inner.stopping.store(false, Ordering::SeqCst);
        inner.status_tx.send_replace(Status::Starting);
        thread::Builder::new()
            .name("core-supervisor".into())
            .spawn(move || supervise(inner, generation))
            .expect("cannot spawn the supervisor thread");
    }

    pub fn status(&self) -> Status {
        self.inner.status_rx.borrow().clone()
    }

    /// The stored port and token, when the core is up.
    pub fn info(&self) -> Option<CoreInfo> {
        match self.status() {
            Status::Ready(i) => Some(i),
            _ => None,
        }
    }

    /// Waits until the core is ready or the shell has given up.
    pub async fn wait_ready(&self, timeout: Duration) -> Result<CoreInfo, String> {
        let mut rx = self.inner.status_rx.clone();
        let wait = rx.wait_for(|s| !matches!(s, Status::Starting));
        match tokio::time::timeout(timeout, wait).await {
            Ok(Ok(s)) => match &*s {
                Status::Ready(i) => Ok(i.clone()),
                Status::Failed(m) => Err(m.clone()),
                Status::Stopped => Err("the core is stopped".into()),
                Status::Starting => unreachable!(),
            },
            Ok(Err(_)) => Err("the core supervisor is gone".into()),
            Err(_) => Err(format!(
                "the core did not become ready within {} s",
                timeout.as_secs()
            )),
        }
    }

    /// Closes stdin, waits up to [`STOP_GRACE`], then kills. Blocking.
    pub fn stop(&self) {
        self.inner.stopping.store(true, Ordering::SeqCst);
        let mut guard = self.inner.running.lock().unwrap_or_else(|p| p.into_inner());
        if let Some(r) = guard.as_mut() {
            log::info!("stopping the core (pid {})", r.pid);
            drop(r.stdin.take());
            let status = reap(&mut r.child, STOP_GRACE);
            log::info!("core (pid {}) stopped: {}", r.pid, describe_exit(&status));
        }
        *guard = None;
        self.inner.status_tx.send_replace(Status::Stopped);
    }

    /// Stops the core and starts it again with a fresh restart budget.
    pub fn restart(&self) {
        self.stop();
        self.inner.generation.fetch_add(1, Ordering::SeqCst);
        self.inner
            .restarts
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .clear();
        self.start();
    }
}

/// Waits up to `grace` for the child to exit on its own, then kills it.
fn reap(child: &mut Child, grace: Duration) -> Option<ExitStatus> {
    let deadline = Instant::now() + grace;
    loop {
        match child.try_wait() {
            Ok(Some(status)) => return Some(status),
            Ok(None) => {}
            Err(_) => return None,
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            return child.wait().ok();
        }
        thread::sleep(POLL);
    }
}

fn describe_exit(status: &Option<ExitStatus>) -> String {
    match status {
        Some(s) => match s.code() {
            Some(c) => format!("exit code {c}"),
            None => "killed by a signal".into(),
        },
        None => "status unknown".into(),
    }
}

fn supervise(inner: Arc<Inner>, generation: u64) {
    let mine = || inner.generation.load(Ordering::SeqCst) == generation;
    loop {
        if inner.stopping.load(Ordering::SeqCst) || !mine() {
            return;
        }
        match launch(&inner) {
            Ok(info) => {
                inner.status_tx.send_replace(Status::Ready(info.clone()));
                (inner.hooks.on_ready)(&info);
            }
            Err(e) => {
                fatal(&inner, &e);
                return;
            }
        }

        // Wait for this core to exit, polling so `stop()` can take the lock.
        let exit = loop {
            thread::sleep(POLL);
            let mut guard = inner.running.lock().unwrap_or_else(|p| p.into_inner());
            match guard.as_mut() {
                None => break None, // stop() reaped it
                Some(r) => match r.child.try_wait() {
                    Ok(Some(status)) => {
                        let pid = r.pid;
                        *guard = None;
                        break Some((pid, status));
                    }
                    Ok(None) => {}
                    Err(e) => {
                        log::warn!("cannot wait for the core: {e}");
                        *guard = None;
                        break None;
                    }
                },
            }
        };

        if inner.stopping.load(Ordering::SeqCst) || !mine() {
            return;
        }
        let Some((pid, status)) = exit else {
            return;
        };
        log::error!(
            "the core (pid {pid}) exited unexpectedly: {}",
            describe_exit(&Some(status))
        );
        inner.status_tx.send_replace(Status::Starting);

        let now = Instant::now();
        let mut restarts = inner.restarts.lock().unwrap_or_else(|p| p.into_inner());
        restarts.retain(|t| now.duration_since(*t) < RESTART_WINDOW);
        if restarts.len() >= RESTART_LIMIT {
            drop(restarts);
            fatal(
                &inner,
                &format!(
                    "the core stopped {} times within a minute (last: {})",
                    RESTART_LIMIT + 1,
                    describe_exit(&Some(status))
                ),
            );
            return;
        }
        restarts.push_back(now);
        log::warn!(
            "restarting the core ({} of {} restarts in the last minute)",
            restarts.len(),
            RESTART_LIMIT
        );
    }
}

fn fatal(inner: &Inner, message: &str) {
    log::error!("giving up on the core: {message}");
    inner
        .status_tx
        .send_replace(Status::Failed(message.to_string()));
    (inner.hooks.on_fatal)(message);
}

/// Kills a stale core, spawns a new one, waits for its ready line.
fn launch(inner: &Inner) -> Result<CoreInfo, String> {
    let cfg = &inner.cfg;
    if !cfg.exe.is_file() {
        return Err(format!(
            "the core executable is missing: {}",
            cfg.exe.display()
        ));
    }
    std::fs::create_dir_all(&cfg.data_dir)
        .map_err(|e| format!("cannot create {}: {e}", cfg.data_dir.display()))?;

    let pid_file = crate::paths::pid_file(&cfg.data_dir);
    match kill_stale(&pid_file, &cfg.exe, &mut SysTable::new()) {
        StaleOutcome::NoPidFile => {}
        StaleOutcome::Unreadable => log::warn!("core.pid held no pid; removed"),
        StaleOutcome::NotRunning(pid) => {
            log::info!("core.pid named pid {pid}, not running; removed")
        }
        StaleOutcome::NotOurs { pid, exe } => log::info!(
            "core.pid named pid {pid}, now {}; removed",
            exe.map(|p| p.display().to_string())
                .unwrap_or_else(|| "an unknown program".into())
        ),
        StaleOutcome::Killed(pid) => log::warn!("killed a stale core (pid {pid})"),
        StaleOutcome::KillFailed(pid) => {
            return Err(format!(
                "a core from an earlier run (pid {pid}) is still running and would not stop"
            ));
        }
    }

    let token = new_token();
    let mut cmd = Command::new(&cfg.exe);
    cmd.env("TESSERA_TOKEN", &token)
        .env("TESSERA_DATA_DIR", &cfg.data_dir)
        .env("TESSERA_WEB_ORIGIN", &cfg.web_origin)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    if let Some(dir) = cfg.exe.parent() {
        cmd.current_dir(dir);
    }
    if cfg.dev {
        cmd.env("TESSERA_DEV", "1");
    }
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(windows_sys::Win32::System::Threading::CREATE_NO_WINDOW);
    }

    log::info!("starting the core: {}", cfg.exe.display());
    let mut child = cmd
        .spawn()
        .map_err(|e| format!("cannot start {}: {e}", cfg.exe.display()))?;
    let pid = child.id();
    let stdin = child.stdin.take();
    let stdout = child.stdout.take().expect("stdout was piped");
    let stderr = child.stderr.take().expect("stderr was piped");

    // stderr: the core's log lines, into the app log as they come.
    thread::Builder::new()
        .name("core-stderr".into())
        .spawn(move || {
            for line in BufReader::new(stderr).lines().map_while(Result::ok) {
                log::info!(target: "core", "{line}");
            }
        })
        .expect("cannot spawn the stderr thread");

    // stdout: the ready line, then nothing (anything else is logged).
    let (tx, rx) = mpsc::channel::<String>();
    thread::Builder::new()
        .name("core-stdout".into())
        .spawn(move || {
            for line in BufReader::new(stdout).lines().map_while(Result::ok) {
                if tx.send(line).is_err() {
                    break;
                }
            }
        })
        .expect("cannot spawn the stdout thread");

    *inner.running.lock().unwrap_or_else(|p| p.into_inner()) = Some(Running { child, stdin, pid });

    let deadline = Instant::now() + READY_TIMEOUT;
    let ready = loop {
        let left = deadline.saturating_duration_since(Instant::now());
        match rx.recv_timeout(left) {
            Ok(line) => {
                if let Some(r) = parse_ready_line(&line) {
                    break r;
                }
                log::warn!(target: "core", "unexpected stdout line: {line}");
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {
                abort_launch(inner);
                return Err(format!(
                    "the core did not report ready within {} s",
                    READY_TIMEOUT.as_secs()
                ));
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                let status = abort_launch(inner);
                return Err(format!(
                    "the core exited before it was ready ({})",
                    describe_exit(&status)
                ));
            }
        }
    };

    // Keep draining stdout so the pipe never fills; log what leaks.
    thread::Builder::new()
        .name("core-stdout-drain".into())
        .spawn(move || {
            for line in rx {
                log::warn!(target: "core", "unexpected stdout line: {line}");
            }
        })
        .expect("cannot spawn the drain thread");

    if ready.pid != pid {
        log::warn!(
            "the core reports pid {} but was spawned as {pid}",
            ready.pid
        );
    }
    log::info!(
        "core {} ready on 127.0.0.1:{} (pid {})",
        ready.version,
        ready.port,
        ready.pid
    );
    Ok(CoreInfo {
        base_url: format!("http://127.0.0.1:{}", ready.port),
        token,
    })
}

/// Kills the child spawned by a launch that did not reach ready.
fn abort_launch(inner: &Inner) -> Option<ExitStatus> {
    let mut guard = inner.running.lock().unwrap_or_else(|p| p.into_inner());
    let status = guard.as_mut().and_then(|r| {
        drop(r.stdin.take());
        reap(&mut r.child, Duration::from_secs(1))
    });
    *guard = None;
    status
}

// ---------------------------------------------------------------- tests

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    #[test]
    fn ready_line_parses_the_contract_shape() {
        let r = parse_ready_line(r#"{"event":"ready","port":51234,"pid":4242,"version":"1.0.0"}"#)
            .expect("ready");
        assert_eq!(
            r,
            Ready {
                port: 51234,
                pid: 4242,
                version: "1.0.0".into()
            }
        );
    }

    #[test]
    fn ready_line_tolerates_whitespace_and_extra_fields() {
        let r = parse_ready_line(
            "  {\"event\":\"ready\",\"port\":5,\"pid\":1,\"version\":\"x\",\"extra\":true}\r\n",
        );
        assert_eq!(r.map(|r| r.port), Some(5));
    }

    #[test]
    fn ready_line_rejects_noise() {
        for bad in [
            "",
            "hello",
            r#"{"event":"log","port":5,"pid":1,"version":"x"}"#,
            r#"{"event":"ready","port":0,"pid":1,"version":"x"}"#,
            r#"{"event":"ready","pid":1,"version":"x"}"#,
            r#"{"event":"ready","port":"5","pid":1,"version":"x"}"#,
            r#"{"event":"ready","port":70000,"pid":1,"version":"x"}"#,
        ] {
            assert!(parse_ready_line(bad).is_none(), "accepted {bad:?}");
        }
    }

    #[test]
    fn token_is_43_chars_of_base64url() {
        let t = new_token();
        assert_eq!(t.len(), 43);
        assert!(
            t.chars()
                .all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_')
        );
        assert_ne!(t, new_token());
    }

    struct FakeTable {
        procs: HashMap<u32, Option<PathBuf>>,
        killed: Vec<u32>,
        kill_works: bool,
    }

    impl FakeTable {
        fn with(procs: &[(u32, Option<&str>)]) -> Self {
            Self {
                procs: procs
                    .iter()
                    .map(|(p, e)| (*p, e.map(PathBuf::from)))
                    .collect(),
                killed: vec![],
                kill_works: true,
            }
        }
    }

    impl ProcessTable for FakeTable {
        fn lookup(&mut self, pid: u32) -> Lookup {
            match self.procs.get(&pid) {
                None => Lookup::Dead,
                Some(exe) => Lookup::Alive(exe.clone()),
            }
        }
        fn kill(&mut self, pid: u32) -> bool {
            self.killed.push(pid);
            if self.kill_works {
                self.procs.remove(&pid);
            }
            self.kill_works
        }
    }

    fn our_exe() -> PathBuf {
        PathBuf::from(if cfg!(windows) {
            r"C:\Program Files\Tessera Desktop\resources\core\tessera-core.exe"
        } else {
            "/Applications/Tessera Desktop.app/Contents/Resources/resources/core/tessera-core"
        })
    }

    #[test]
    fn stale_pid_no_file() {
        let dir = tempfile::tempdir().unwrap();
        let mut t = FakeTable::with(&[]);
        assert_eq!(
            kill_stale(&dir.path().join("core.pid"), &our_exe(), &mut t),
            StaleOutcome::NoPidFile
        );
    }

    #[test]
    fn stale_pid_garbage_is_removed() {
        let dir = tempfile::tempdir().unwrap();
        let f = dir.path().join("core.pid");
        std::fs::write(&f, "not a pid").unwrap();
        let mut t = FakeTable::with(&[]);
        assert_eq!(kill_stale(&f, &our_exe(), &mut t), StaleOutcome::Unreadable);
        assert!(!f.exists());
    }

    #[test]
    fn stale_pid_dead_process_is_cleaned_up() {
        let dir = tempfile::tempdir().unwrap();
        let f = dir.path().join("core.pid");
        std::fs::write(&f, "4242\n").unwrap();
        let mut t = FakeTable::with(&[]);
        assert_eq!(
            kill_stale(&f, &our_exe(), &mut t),
            StaleOutcome::NotRunning(4242)
        );
        assert!(t.killed.is_empty());
        assert!(!f.exists());
    }

    #[test]
    fn stale_pid_our_core_is_killed_even_from_an_older_install_dir() {
        let dir = tempfile::tempdir().unwrap();
        let f = dir.path().join("core.pid");
        std::fs::write(&f, "4242").unwrap();
        let old = if cfg!(windows) {
            r"C:\Users\me\AppData\Local\Tessera Desktop\resources\core\TESSERA-CORE.EXE"
        } else {
            "/Users/me/old/Tessera Desktop.app/Contents/Resources/resources/core/tessera-core"
        };
        let mut t = FakeTable::with(&[(4242, Some(old))]);
        assert_eq!(
            kill_stale(&f, &our_exe(), &mut t),
            StaleOutcome::Killed(4242)
        );
        assert_eq!(t.killed, vec![4242]);
        assert!(!f.exists());
    }

    #[test]
    fn stale_pid_reused_by_another_program_is_left_alone() {
        let dir = tempfile::tempdir().unwrap();
        let f = dir.path().join("core.pid");
        std::fs::write(&f, "4242").unwrap();
        let other = if cfg!(windows) {
            r"C:\Windows\explorer.exe"
        } else {
            "/usr/bin/vim"
        };
        let mut t = FakeTable::with(&[(4242, Some(other))]);
        assert_eq!(
            kill_stale(&f, &our_exe(), &mut t),
            StaleOutcome::NotOurs {
                pid: 4242,
                exe: Some(PathBuf::from(other))
            }
        );
        assert!(t.killed.is_empty());
        assert!(!f.exists(), "a reused pid means the file is stale");
    }

    #[test]
    fn stale_pid_unknown_exe_is_not_killed() {
        let dir = tempfile::tempdir().unwrap();
        let f = dir.path().join("core.pid");
        std::fs::write(&f, "4242").unwrap();
        let mut t = FakeTable::with(&[(4242, None)]);
        assert_eq!(
            kill_stale(&f, &our_exe(), &mut t),
            StaleOutcome::NotOurs {
                pid: 4242,
                exe: None
            }
        );
        assert!(t.killed.is_empty());
    }

    #[test]
    fn stale_pid_kill_failure_keeps_the_file() {
        let dir = tempfile::tempdir().unwrap();
        let f = dir.path().join("core.pid");
        std::fs::write(&f, "4242").unwrap();
        let mut t = FakeTable::with(&[(4242, Some(our_exe().to_str().unwrap()))]);
        t.kill_works = false;
        assert_eq!(
            kill_stale(&f, &our_exe(), &mut t),
            StaleOutcome::KillFailed(4242)
        );
        assert!(f.exists());
    }

    #[test]
    fn stale_pid_never_kills_the_shell_itself() {
        let dir = tempfile::tempdir().unwrap();
        let f = dir.path().join("core.pid");
        std::fs::write(&f, std::process::id().to_string()).unwrap();
        let mut t = FakeTable::with(&[(std::process::id(), Some(our_exe().to_str().unwrap()))]);
        assert_eq!(kill_stale(&f, &our_exe(), &mut t), StaleOutcome::Unreadable);
        assert!(t.killed.is_empty());
    }
}
