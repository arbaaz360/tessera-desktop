//! Licence (docs/CONTRACT.md section 5, docs/PLAN.md section 6): verify a key
//! with Gumroad, keep the key in the OS keychain, keep the state and the time
//! of the last check in `<data>/licence.json`, tell the core.
//!
//! Follows the Memories Rescue rules (SRS section 9.3): activation is the one
//! check that increments Gumroad's use count; the 30-day re-check sends
//! `increment_uses_count=false`; a transport error, a 429 or a 5xx leaves the
//! stored state untouched; only a well-formed "no" from Gumroad revokes.

use crate::core::CoreInfo;
use serde::{Deserialize, Serialize};
use std::{
    path::{Path, PathBuf},
    time::Duration,
};

pub const GUMROAD_VERIFY_URL: &str = "https://api.gumroad.com/v2/licenses/verify";
pub const PRODUCT_ID_PLACEHOLDER: &str = "PLACEHOLDER_GUMROAD_PRODUCT_ID";
/// The Gumroad product id, baked in at build time from
/// `TESSERA_GUMROAD_PRODUCT_ID`; the placeholder until the product exists.
pub const PRODUCT_ID: &str = match option_env!("TESSERA_GUMROAD_PRODUCT_ID") {
    Some(v) => v,
    None => PRODUCT_ID_PLACEHOLDER,
};
/// A licensed install re-checks its key after this long.
pub const RECHECK_AFTER: Duration = Duration::from_secs(30 * 24 * 60 * 60);
const HTTP_TIMEOUT: Duration = Duration::from_secs(15);
const CORE_TIMEOUT: Duration = Duration::from_secs(5);

pub const STATE_FREE: &str = "free";
pub const STATE_LICENSED: &str = "licensed";

// ---------------------------------------------------------------- Gumroad

/// What one verify call means for the licence.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Verdict {
    /// Gumroad says yes and the purchase is neither refunded nor charged back.
    Valid,
    /// A well-formed "no": unknown key, disabled key, refunded or charged back.
    Invalid,
    /// No usable answer: transport error, timeout, 429, 5xx, not Gumroad's JSON.
    Offline,
}

/// Maps a Gumroad answer to a verdict. Pure, so it is unit-tested directly.
pub fn map_response(status: u16, body: &[u8]) -> Verdict {
    if status == 429 || status >= 500 {
        return Verdict::Offline;
    }
    let Ok(v) = serde_json::from_slice::<serde_json::Value>(body) else {
        return Verdict::Offline;
    };
    match v.get("success").and_then(|s| s.as_bool()) {
        Some(true) => {
            let flag = |k: &str| {
                v.get("purchase")
                    .and_then(|p| p.get(k))
                    .and_then(|b| b.as_bool())
                    .unwrap_or(false)
            };
            if flag("refunded") || flag("chargebacked") {
                Verdict::Invalid
            } else {
                Verdict::Valid
            }
        }
        Some(false) => Verdict::Invalid,
        None => Verdict::Offline,
    }
}

/// `POST <url>` with Gumroad's form fields.
pub async fn verify(
    client: &reqwest::Client,
    url: &str,
    product_id: &str,
    key: &str,
    increment_uses_count: bool,
) -> Verdict {
    let form = [
        ("product_id", product_id),
        ("license_key", key),
        (
            "increment_uses_count",
            if increment_uses_count {
                "true"
            } else {
                "false"
            },
        ),
    ];
    let resp = match client.post(url).form(&form).send().await {
        Ok(r) => r,
        Err(e) => {
            log::warn!("licence check: no answer from Gumroad: {e}");
            return Verdict::Offline;
        }
    };
    let status = resp.status().as_u16();
    let body = match resp.bytes().await {
        Ok(b) => b,
        Err(e) => {
            log::warn!("licence check: body unreadable: {e}");
            return Verdict::Offline;
        }
    };
    let verdict = map_response(status, &body);
    log::info!("licence check: HTTP {status} -> {verdict:?}");
    verdict
}

// ---------------------------------------------------------------- storage

/// `<data>/licence.json`. The key itself is never here; it is in the keychain.
#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", default)]
pub struct Stored {
    pub state: String,
    pub checked_at: Option<String>,
    pub key_masked: Option<String>,
}

impl Stored {
    pub fn free() -> Self {
        Self {
            state: STATE_FREE.into(),
            checked_at: None,
            key_masked: None,
        }
    }

    pub fn is_licensed(&self) -> bool {
        self.state == STATE_LICENSED
    }

    /// True when the last check is older than [`RECHECK_AFTER`] or unparseable.
    pub fn recheck_due(&self, now: chrono::DateTime<chrono::Utc>) -> bool {
        if !self.is_licensed() {
            return false;
        }
        let Some(at) = self.checked_at.as_deref() else {
            return true;
        };
        match chrono::DateTime::parse_from_rfc3339(at) {
            Ok(t) => {
                let age = now.signed_duration_since(t.with_timezone(&chrono::Utc));
                age.to_std().map(|d| d >= RECHECK_AFTER).unwrap_or(false)
            }
            Err(_) => true,
        }
    }
}

pub fn now_iso() -> String {
    chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Secs, true)
}

/// `XXXX-…-1234`: only the last four characters survive.
pub fn mask_key(key: &str) -> String {
    let chars: Vec<char> = key.chars().collect();
    let tail: String = chars[chars.len().saturating_sub(4)..].iter().collect();
    format!("XXXX-…-{tail}")
}

pub struct Store {
    path: PathBuf,
}

impl Store {
    pub fn new(path: PathBuf) -> Self {
        Self { path }
    }

    pub fn load(&self) -> Stored {
        match std::fs::read(&self.path) {
            Ok(bytes) => serde_json::from_slice(&bytes).unwrap_or_else(|e| {
                log::warn!("licence.json unreadable ({e}); treating as free");
                Stored::free()
            }),
            Err(_) => Stored::free(),
        }
    }

    pub fn save(&self, s: &Stored) -> std::io::Result<()> {
        if let Some(dir) = self.path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let tmp = self.path.with_extension("json.tmp");
        std::fs::write(&tmp, serde_json::to_vec_pretty(s)?)?;
        std::fs::rename(&tmp, &self.path)
    }
}

// ---------------------------------------------------------------- keychain

const KEYRING_SERVICE: &str = "TesseraDesktop";
const KEYRING_USER: &str = "licence-key";

fn keychain_entry() -> Result<keyring::Entry, String> {
    keyring::Entry::new(KEYRING_SERVICE, KEYRING_USER).map_err(|e| format!("keychain: {e}"))
}

pub fn keychain_set(key: &str) -> Result<(), String> {
    keychain_entry()?
        .set_password(key)
        .map_err(|e| format!("keychain: {e}"))
}

pub fn keychain_get() -> Result<Option<String>, String> {
    match keychain_entry()?.get_password() {
        Ok(k) => Ok(Some(k)),
        Err(keyring::Error::NoEntry) => Ok(None),
        Err(e) => Err(format!("keychain: {e}")),
    }
}

// ---------------------------------------------------------------- the licence

/// What `licence_get()` answers.
#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Answer {
    pub state: String,
    pub key_masked: Option<String>,
    pub checked_at: Option<String>,
}

/// What `licence_activate(key)` answers.
#[derive(Clone, Debug, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ActivateAnswer {
    pub state: String,
    pub error: Option<String>,
}

pub struct Licence {
    store: Store,
    client: reqwest::Client,
    verify_url: String,
    product_id: String,
}

impl Licence {
    pub fn new(data_dir: &Path) -> Self {
        Self::with(
            Store::new(crate::paths::licence_file(data_dir)),
            GUMROAD_VERIFY_URL.into(),
            PRODUCT_ID.into(),
        )
    }

    pub fn with(store: Store, verify_url: String, product_id: String) -> Self {
        let client = reqwest::Client::builder()
            .timeout(HTTP_TIMEOUT)
            .user_agent(concat!("TesseraDesktop/", env!("CARGO_PKG_VERSION")))
            .build()
            .expect("reqwest client");
        Self {
            store,
            client,
            verify_url,
            product_id,
        }
    }

    pub fn get(&self) -> Answer {
        let s = self.store.load();
        Answer {
            state: s.state,
            key_masked: s.key_masked,
            checked_at: s.checked_at,
        }
    }

    /// Verifies with Gumroad (counting the use), stores the key and the
    /// state, tells the core. `core` is `None` when the core is not up; the
    /// state is pushed again on the next core start.
    pub async fn activate(&self, key: &str, core: Option<&CoreInfo>) -> ActivateAnswer {
        let key = key.trim();
        let current = self.store.load();
        if key.is_empty() {
            return ActivateAnswer {
                state: current.state,
                error: Some("invalid".into()),
            };
        }
        if self.product_id == PRODUCT_ID_PLACEHOLDER {
            log::warn!("licence: no Gumroad product id was built in; every key is invalid");
        }
        match verify(&self.client, &self.verify_url, &self.product_id, key, true).await {
            Verdict::Valid => {
                if let Err(e) = keychain_set(key) {
                    log::error!("licence: cannot store the key: {e}");
                }
                let stored = Stored {
                    state: STATE_LICENSED.into(),
                    checked_at: Some(now_iso()),
                    key_masked: Some(mask_key(key)),
                };
                if let Err(e) = self.store.save(&stored) {
                    log::error!("licence: cannot write licence.json: {e}");
                }
                if let Some(core) = core {
                    self.push_to_core(core, STATE_LICENSED).await;
                }
                ActivateAnswer {
                    state: STATE_LICENSED.into(),
                    error: None,
                }
            }
            Verdict::Invalid => ActivateAnswer {
                state: current.state,
                error: Some("invalid".into()),
            },
            Verdict::Offline => ActivateAnswer {
                state: current.state,
                error: Some("offline".into()),
            },
        }
    }

    /// `PUT /api/licence` on the core with the bearer token. Logged, never fatal.
    pub async fn push_to_core(&self, core: &CoreInfo, state: &str) {
        let url = format!("{}/api/licence", core.base_url);
        let r = self
            .client
            .put(&url)
            .timeout(CORE_TIMEOUT)
            .bearer_auth(&core.token)
            .json(&serde_json::json!({ "state": state }))
            .send()
            .await;
        match r {
            Ok(resp) if resp.status().is_success() => {
                log::info!("licence: core now {state}");
            }
            Ok(resp) => log::warn!(
                "licence: core answered {} to PUT /api/licence",
                resp.status()
            ),
            Err(e) => log::warn!("licence: cannot reach the core: {e}"),
        }
    }

    /// Runs when a core comes up: pushes the stored state, then re-checks a
    /// licence older than 30 days without counting a use.
    pub async fn sync_core(&self, core: &CoreInfo) {
        let stored = self.store.load();
        self.push_to_core(core, &stored.state).await;
        if !stored.recheck_due(chrono::Utc::now()) {
            return;
        }
        let key = match keychain_get() {
            Ok(Some(k)) => k,
            Ok(None) => {
                log::warn!("licence: licensed but no key in the keychain; skipping the re-check");
                return;
            }
            Err(e) => {
                log::warn!("licence: {e}; skipping the re-check");
                return;
            }
        };
        match verify(
            &self.client,
            &self.verify_url,
            &self.product_id,
            &key,
            false,
        )
        .await
        {
            Verdict::Valid => {
                let s = Stored {
                    checked_at: Some(now_iso()),
                    ..stored
                };
                if let Err(e) = self.store.save(&s) {
                    log::error!("licence: cannot write licence.json: {e}");
                }
            }
            Verdict::Invalid => {
                log::warn!("licence: Gumroad revoked the key; back to the free tier");
                let s = Stored {
                    state: STATE_FREE.into(),
                    checked_at: Some(now_iso()),
                    key_masked: stored.key_masked,
                };
                if let Err(e) = self.store.save(&s) {
                    log::error!("licence: cannot write licence.json: {e}");
                }
                self.push_to_core(core, STATE_FREE).await;
            }
            Verdict::Offline => {
                log::info!("licence: re-check postponed, Gumroad unreachable");
            }
        }
    }
}

// ---------------------------------------------------------------- tests

#[cfg(test)]
mod tests {
    use super::*;
    use std::{sync::Arc, thread};
    use tiny_http::{Header, Response, Server};

    #[test]
    fn mapping_follows_the_memories_rescue_rules() {
        let ok = br#"{"success":true,"uses":1,"purchase":{"refunded":false,"chargebacked":false}}"#;
        assert_eq!(map_response(200, ok), Verdict::Valid);
        assert_eq!(
            map_response(200, br#"{"success":true,"purchase":{"refunded":true}}"#),
            Verdict::Invalid
        );
        assert_eq!(
            map_response(200, br#"{"success":true,"purchase":{"chargebacked":true}}"#),
            Verdict::Invalid
        );
        assert_eq!(
            map_response(
                404,
                br#"{"success":false,"message":"That license does not exist for the provided product."}"#
            ),
            Verdict::Invalid
        );
        assert_eq!(map_response(200, br#"{"success":false}"#), Verdict::Invalid);
        assert_eq!(map_response(429, br#"{"success":false}"#), Verdict::Offline);
        assert_eq!(map_response(500, br#"{"success":false}"#), Verdict::Offline);
        assert_eq!(
            map_response(502, b"<html>bad gateway</html>"),
            Verdict::Offline
        );
        assert_eq!(
            map_response(200, b"<html>captive portal</html>"),
            Verdict::Offline
        );
        assert_eq!(map_response(200, br#"{"ok":true}"#), Verdict::Offline);
    }

    #[test]
    fn mask_keeps_only_the_tail() {
        assert_eq!(
            mask_key("ABCD1234-EFGH5678-IJKL9012-MNOP3456"),
            "XXXX-…-3456"
        );
        assert_eq!(mask_key("ab"), "XXXX-…-ab");
    }

    #[test]
    fn recheck_is_due_after_thirty_days() {
        let now = chrono::Utc::now();
        let fresh = Stored {
            state: STATE_LICENSED.into(),
            checked_at: Some((now - chrono::Duration::days(29)).to_rfc3339()),
            key_masked: None,
        };
        assert!(!fresh.recheck_due(now));
        let old = Stored {
            checked_at: Some((now - chrono::Duration::days(31)).to_rfc3339()),
            ..fresh.clone()
        };
        assert!(old.recheck_due(now));
        let never = Stored {
            checked_at: None,
            ..fresh.clone()
        };
        assert!(never.recheck_due(now));
        assert!(!Stored::free().recheck_due(now));
    }

    #[test]
    fn store_round_trips_and_defaults_to_free() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::new(dir.path().join("licence.json"));
        assert_eq!(store.load(), Stored::free());
        let s = Stored {
            state: STATE_LICENSED.into(),
            checked_at: Some("2026-10-16T00:00:00Z".into()),
            key_masked: Some("XXXX-…-3456".into()),
        };
        store.save(&s).unwrap();
        assert_eq!(store.load(), s);
        std::fs::write(dir.path().join("licence.json"), b"{broken").unwrap();
        assert_eq!(store.load(), Stored::free());
    }

    /// A fake Gumroad on loopback: answers every request with one canned
    /// (status, body) and records what it received.
    struct FakeGumroad {
        url: String,
        seen: Arc<std::sync::Mutex<Vec<String>>>,
    }

    fn fake_gumroad(status: u16, body: &'static str) -> FakeGumroad {
        let server = Server::http("127.0.0.1:0").unwrap();
        let port = server.server_addr().to_ip().unwrap().port();
        let seen = Arc::new(std::sync::Mutex::new(Vec::new()));
        let seen2 = seen.clone();
        thread::spawn(move || {
            for mut req in server.incoming_requests() {
                let mut b = String::new();
                let _ = req.as_reader().read_to_string(&mut b);
                seen2.lock().unwrap().push(b);
                let resp = Response::from_string(body)
                    .with_status_code(status)
                    .with_header(Header::from_bytes("Content-Type", "application/json").unwrap());
                let _ = req.respond(resp);
            }
        });
        FakeGumroad {
            url: format!("http://127.0.0.1:{port}/v2/licenses/verify"),
            seen,
        }
    }

    fn rt() -> tokio::runtime::Runtime {
        tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap()
    }

    #[test]
    fn verify_sends_gumroad_form_fields_and_maps_the_answer() {
        let g = fake_gumroad(
            200,
            r#"{"success":true,"uses":1,"purchase":{"refunded":false,"chargebacked":false}}"#,
        );
        let client = reqwest::Client::new();
        let v = rt().block_on(verify(&client, &g.url, "prod_123", "KEY-1", true));
        assert_eq!(v, Verdict::Valid);
        let seen = g.seen.lock().unwrap();
        assert_eq!(seen.len(), 1);
        assert!(seen[0].contains("product_id=prod_123"), "{}", seen[0]);
        assert!(seen[0].contains("license_key=KEY-1"), "{}", seen[0]);
        assert!(seen[0].contains("increment_uses_count=true"), "{}", seen[0]);
    }

    #[test]
    fn verify_recheck_does_not_count_a_use() {
        let g = fake_gumroad(200, r#"{"success":true,"purchase":{}}"#);
        let client = reqwest::Client::new();
        let v = rt().block_on(verify(&client, &g.url, "p", "k", false));
        assert_eq!(v, Verdict::Valid);
        assert!(g.seen.lock().unwrap()[0].contains("increment_uses_count=false"));
    }

    #[test]
    fn verify_maps_invalid_offline_and_unreachable() {
        let client = reqwest::Client::new();
        let g = fake_gumroad(404, r#"{"success":false,"message":"nope"}"#);
        assert_eq!(
            rt().block_on(verify(&client, &g.url, "p", "k", true)),
            Verdict::Invalid
        );
        let g = fake_gumroad(503, "down");
        assert_eq!(
            rt().block_on(verify(&client, &g.url, "p", "k", true)),
            Verdict::Offline
        );
        // A port nobody listens on.
        let dead = {
            let l = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
            let p = l.local_addr().unwrap().port();
            drop(l);
            format!("http://127.0.0.1:{p}/verify")
        };
        assert_eq!(
            rt().block_on(verify(&client, &dead, "p", "k", true)),
            Verdict::Offline
        );
    }

    #[test]
    fn activate_answers_per_contract_without_touching_the_core() {
        let dir = tempfile::tempdir().unwrap();
        let store = Store::new(dir.path().join("licence.json"));
        let g = fake_gumroad(404, r#"{"success":false}"#);
        let lic = Licence::with(store, g.url.clone(), "p".into());
        let a = rt().block_on(lic.activate("BAD", None));
        assert_eq!(
            a,
            ActivateAnswer {
                state: "free".into(),
                error: Some("invalid".into())
            }
        );
        assert_eq!(lic.get().state, "free");

        let g = fake_gumroad(500, "");
        let lic = Licence::with(
            Store::new(dir.path().join("licence.json")),
            g.url.clone(),
            "p".into(),
        );
        let a = rt().block_on(lic.activate("ANY", None));
        assert_eq!(
            a,
            ActivateAnswer {
                state: "free".into(),
                error: Some("offline".into())
            }
        );

        let before = g.seen.lock().unwrap().len();
        let a = rt().block_on(lic.activate("   ", None));
        assert_eq!(a.error.as_deref(), Some("invalid"));
        assert_eq!(
            g.seen.lock().unwrap().len(),
            before,
            "an empty key never reaches Gumroad"
        );
    }
}
