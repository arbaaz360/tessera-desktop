//! A stand-in for `tessera-core` (docs/CONTRACT.md section 1) so the shell can
//! be tested before the real core exists. It:
//!
//! - writes `core.pid` in `TESSERA_DATA_DIR`,
//! - listens on 127.0.0.1 (`TESSERA_PORT` or a free port),
//! - prints the one ready line on stdout,
//! - serves `/health/live`, `/health/ready`, `GET|PUT /api/licence` (token
//!   checked, CORS for `TESSERA_WEB_ORIGIN`),
//! - logs one JSON object per line on stderr,
//! - exits when stdin reaches EOF (`STUB_IGNORE_STDIN=1` turns that off, to
//!   leave an orphan behind on purpose and watch the shell kill it),
//! - `STUB_CRASH_AFTER_MS=<n>` makes it exit with code 7 after n ms, to watch
//!   the restart policy,
//! - `STUB_SILENT=1` never prints the ready line, to watch the ready timeout.

use std::{
    env,
    io::Read,
    path::PathBuf,
    process,
    sync::{Arc, Mutex},
    thread,
    time::Duration,
};
use tiny_http::{Header, Method, Request, Response, Server};

fn log(level: &str, msg: &str) {
    eprintln!(
        "{{\"ts\":{},\"level\":\"{level}\",\"msg\":{}}}",
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis())
            .unwrap_or(0),
        serde_json::to_string(msg).unwrap_or_default()
    );
}

fn header(name: &str, value: &str) -> Header {
    Header::from_bytes(name, value).expect("header")
}

fn json(status: u16, body: String) -> Response<std::io::Cursor<Vec<u8>>> {
    Response::from_string(body)
        .with_status_code(status)
        .with_header(header("Content-Type", "application/json"))
}

fn main() {
    let token = env::var("TESSERA_TOKEN").unwrap_or_default();
    let data_dir = PathBuf::from(env::var("TESSERA_DATA_DIR").unwrap_or_else(|_| ".".into()));
    let web_origin = env::var("TESSERA_WEB_ORIGIN").unwrap_or_default();
    let dev = env::var("TESSERA_DEV").map(|v| v == "1").unwrap_or(false);
    let port: u16 = env::var("TESSERA_PORT")
        .ok()
        .and_then(|p| p.parse().ok())
        .unwrap_or(0);

    if token.len() != 43 {
        log(
            "warn",
            &format!("TESSERA_TOKEN has {} chars, expected 43", token.len()),
        );
    }
    if let Err(e) = std::fs::create_dir_all(&data_dir) {
        log("error", &format!("cannot create data dir: {e}"));
        process::exit(1);
    }
    let pid_file = data_dir.join("core.pid");
    if let Err(e) = std::fs::write(&pid_file, process::id().to_string()) {
        log("error", &format!("cannot write core.pid: {e}"));
        process::exit(1);
    }

    let server = match Server::http(("127.0.0.1", port)) {
        Ok(s) => s,
        Err(e) => {
            log("error", &format!("cannot listen: {e}"));
            process::exit(1);
        }
    };
    let port = server.server_addr().to_ip().expect("ip").port();

    // The one line on stdout, ever. `STUB_SILENT=1` withholds it, to watch
    // the shell's 30 s ready timeout.
    if env::var("STUB_SILENT").is_ok() {
        log("warn", "STUB_SILENT set; not printing the ready line");
    } else {
        println!(
            "{{\"event\":\"ready\",\"port\":{port},\"pid\":{},\"version\":\"0.0.0-stub\"}}",
            process::id()
        );
    }
    log(
        "info",
        &format!(
            "stub core listening on 127.0.0.1:{port}, data dir {}",
            data_dir.display()
        ),
    );

    let pid_file_for_exit = pid_file.clone();
    if env::var("STUB_IGNORE_STDIN").is_err() {
        thread::spawn(move || {
            let mut sink = Vec::new();
            let _ = std::io::stdin().read_to_end(&mut sink);
            log("info", "stdin closed; shutting down");
            let _ = std::fs::remove_file(&pid_file_for_exit);
            process::exit(0);
        });
    } else {
        log(
            "warn",
            "STUB_IGNORE_STDIN set; this stub will outlive its parent",
        );
    }
    if let Some(ms) = env::var("STUB_CRASH_AFTER_MS")
        .ok()
        .and_then(|v| v.parse::<u64>().ok())
    {
        thread::spawn(move || {
            thread::sleep(Duration::from_millis(ms));
            log("error", "simulated crash");
            process::exit(7);
        });
    }

    let state = Arc::new(Mutex::new(String::from("free")));
    let allowed_origins: Vec<String> = {
        let mut v = vec![web_origin];
        if dev {
            v.push("http://localhost:5173".into());
        }
        v.into_iter().filter(|o| !o.is_empty()).collect()
    };

    for req in server.incoming_requests() {
        handle(req, &token, &allowed_origins, &state);
    }
}

fn handle(mut req: Request, token: &str, origins: &[String], state: &Mutex<String>) {
    let origin = req
        .headers()
        .iter()
        .find(|h| h.field.equiv("Origin"))
        .map(|h| h.value.as_str().to_string());
    let cors: Vec<Header> = match origin {
        Some(o) if origins.iter().any(|a| a == &o) => vec![
            header("Access-Control-Allow-Origin", &o),
            header(
                "Access-Control-Allow-Headers",
                "Authorization, Content-Type",
            ),
            header(
                "Access-Control-Allow-Methods",
                "GET, PUT, POST, DELETE, OPTIONS",
            ),
            header("Vary", "Origin"),
        ],
        _ => vec![],
    };
    let url = req.url().to_string();
    let path = url.split('?').next().unwrap_or("").to_string();
    let query = url.split('?').nth(1).unwrap_or("").to_string();
    let method = req.method().clone();

    // Token: `Authorization: Bearer <token>` or `?sessionKey=<token>`.
    let bearer = req
        .headers()
        .iter()
        .find(|h| h.field.equiv("Authorization"))
        .map(|h| h.value.as_str().trim().to_string())
        .and_then(|v| v.strip_prefix("Bearer ").map(|s| s.to_string()));
    let session_key = query
        .split('&')
        .find_map(|kv| kv.strip_prefix("sessionKey=").map(|s| s.to_string()));
    let presented = bearer.or(session_key);
    let authorised = !token.is_empty() && presented.as_deref() == Some(token);

    let mut body = String::new();
    if method == Method::Put || method == Method::Post {
        let _ = req.as_reader().read_to_string(&mut body);
    }

    let resp = route(method.clone(), &path, authorised, &body, state);
    log(
        "info",
        &format!("{method} {path} -> {}", resp.status_code().0),
    );
    let resp = cors.into_iter().fold(resp, |r, h| r.with_header(h));
    let _ = req.respond(resp);
}

fn route(
    method: Method,
    path: &str,
    authorised: bool,
    body: &str,
    state: &Mutex<String>,
) -> Response<std::io::Cursor<Vec<u8>>> {
    if method == Method::Options {
        return Response::from_string("").with_status_code(204);
    }
    if path == "/health/live" || path == "/health/ready" {
        return json(200, r#"{"ok":true}"#.into());
    }
    if !path.starts_with("/api/") {
        return json(404, r#"{"detail":"not found"}"#.into());
    }
    if !authorised {
        return json(401, r#"{"detail":"missing or wrong token"}"#.into());
    }
    match (method, path) {
        (Method::Get, "/api/licence") => {
            let s = state.lock().unwrap().clone();
            json(200, licence_body(&s))
        }
        (Method::Put, "/api/licence") => {
            let v: serde_json::Value = match serde_json::from_str(body) {
                Ok(v) => v,
                Err(_) => return json(400, r#"{"detail":"body is not JSON"}"#.into()),
            };
            match v.get("state").and_then(|s| s.as_str()) {
                Some(s @ ("free" | "licensed")) => {
                    *state.lock().unwrap() = s.to_string();
                    log("info", &format!("licence state now {s}"));
                    json(200, licence_body(s))
                }
                _ => json(400, r#"{"detail":"state must be free or licensed"}"#.into()),
            }
        }
        _ => json(404, r#"{"detail":"not found"}"#.into()),
    }
}

fn licence_body(state: &str) -> String {
    format!(r#"{{"state":"{state}","cap":2000,"indexed":0,"capReached":false}}"#)
}
