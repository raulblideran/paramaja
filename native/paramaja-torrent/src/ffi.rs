//! C ABI used by the C# app via P/Invoke.
//!
//! Every call returns a heap-allocated UTF-8 JSON string — `{"ok":true,"data":...}` or
//! `{"ok":false,"error":"..."}` — which the caller must release with `nt_free_string`.

use std::{
    ffi::{CStr, CString, c_char},
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{Arc, Mutex},
    time::Duration,
};

use serde::Serialize;
use serde_json::json;

use crate::{Engine, EngineConfig};

static ENGINE: Mutex<Option<Arc<Engine>>> = Mutex::new(None);

fn respond<T: Serialize>(f: impl FnOnce() -> anyhow::Result<T>) -> *mut c_char {
    let value = match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(data)) => json!({ "ok": true, "data": data }),
        Ok(Err(e)) => json!({ "ok": false, "error": format!("{e:#}") }),
        Err(_) => json!({ "ok": false, "error": "internal panic in paramaja-torrent" }),
    };
    CString::new(value.to_string()).unwrap_or_default().into_raw()
}

unsafe fn str_arg<'a>(p: *const c_char) -> anyhow::Result<&'a str> {
    anyhow::ensure!(!p.is_null(), "null string argument");
    Ok(unsafe { CStr::from_ptr(p) }.to_str()?)
}

/// Clones the engine out of the lock so slow calls (e.g. magnet resolution) don't block others.
fn with_engine<T>(f: impl FnOnce(&Engine) -> anyhow::Result<T>) -> anyhow::Result<T> {
    let engine = ENGINE
        .lock()
        .map_err(|_| anyhow::anyhow!("engine lock poisoned"))?
        .clone()
        .ok_or_else(|| anyhow::anyhow!("torrent engine not started"))?;
    f(&engine)
}

/// Starts the engine. `config_json` is an `EngineConfig` (all fields optional).
/// Returns `{"http_addr": "...", "listen_addr": "..."}`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nt_init(config_json: *const c_char) -> *mut c_char {
    respond(|| {
        let cfg: EngineConfig = serde_json::from_str(unsafe { str_arg(config_json)? })?;
        let mut guard = ENGINE.lock().map_err(|_| anyhow::anyhow!("engine lock poisoned"))?;
        if guard.is_none() {
            *guard = Some(Arc::new(Engine::start(cfg)?));
        }
        let e = guard.as_ref().unwrap();
        Ok(json!({ "http_addr": e.http_addr().to_string(),
                   "listen_addr": e.listen_addr().map(|a| a.to_string()) }))
    })
}

/// Adds a magnet link / info hash and waits (up to `timeout_ms`) for its metadata.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nt_add(magnet: *const c_char, timeout_ms: u64) -> *mut c_char {
    respond(|| {
        let magnet = unsafe { str_arg(magnet)? };
        with_engine(|e| e.add(magnet, Duration::from_millis(timeout_ms)))
    })
}

/// Switches which file is downloaded/streamed. Returns the stream URL.
#[unsafe(no_mangle)]
pub extern "C" fn nt_select(id: usize, file: usize) -> *mut c_char {
    respond(|| with_engine(|e| e.select_file(id, file)))
}

#[unsafe(no_mangle)]
pub extern "C" fn nt_stats(id: usize) -> *mut c_char {
    respond(|| with_engine(|e| e.stats(id)))
}

#[unsafe(no_mangle)]
pub extern "C" fn nt_remove(id: usize, delete_files: bool) -> *mut c_char {
    respond(|| with_engine(|e| e.remove(id, delete_files)))
}

#[unsafe(no_mangle)]
pub extern "C" fn nt_shutdown() -> *mut c_char {
    respond(|| {
        // Dropping the last reference stops the session and its runtime.
        drop(ENGINE.lock().map_err(|_| anyhow::anyhow!("engine lock poisoned"))?.take());
        Ok(())
    })
}

/// # Safety
/// `s` must be a pointer previously returned by one of the `nt_*` functions.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nt_free_string(s: *mut c_char) {
    if !s.is_null() {
        drop(unsafe { CString::from_raw(s) });
    }
}
