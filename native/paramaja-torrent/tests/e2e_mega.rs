//! Offline end-to-end test for MEGA links: a fake MEGA API and CDN on localhost serve an
//! encrypted copy of a test clip; the engine resolves the link and the stream server
//! decrypts it, including for Range requests and after the download URL expires.

use std::{
    net::Ipv4Addr,
    path::Path,
    process::Command,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    },
    time::Duration,
};

use aes::{
    Aes128,
    cipher::{BlockEncryptMut, KeyIvInit, StreamCipher},
};
use axum::{
    Json, Router,
    extract::{Path as UrlPath, State},
    http::StatusCode,
    response::IntoResponse,
    routing::{get, post},
};
use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
use paramaja_torrent::{Engine, EngineConfig};
use serde_json::{Value, json};

const RAW_KEY: [u8; 32] = [
    0x10, 0x32, 0x54, 0x76, 0x98, 0xba, 0xdc, 0xfe, 0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef,
    0xf0, 0xe1, 0xd2, 0xc3, 0xb4, 0xa5, 0x96, 0x87, 0x78, 0x69, 0x5a, 0x4b, 0x3c, 0x2d, 0x1e, 0x0f,
];

fn file_key() -> ([u8; 16], [u8; 16]) {
    let mut k = [0u8; 16];
    for i in 0..16 {
        k[i] = RAW_KEY[i] ^ RAW_KEY[i + 16];
    }
    let mut iv = [0u8; 16];
    iv[..8].copy_from_slice(&RAW_KEY[16..24]);
    (k, iv)
}

fn plain() -> Vec<u8> {
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/fixtures/media/sdr_h264.mp4");
    std::fs::read(fixture).unwrap_or_else(|_| (0..300_000u32).map(|i| (i * 7 % 256) as u8).collect())
}

fn encrypted_attributes(name: &str, key: &[u8; 16]) -> String {
    let mut data = format!(r#"MEGA{{"n":"{name}"}}"#).into_bytes();
    data.resize(data.len().div_ceil(16) * 16, 0);
    let mut enc = cbc::Encryptor::<Aes128>::new(key.into(), &[0u8; 16].into());
    for block in data.chunks_exact_mut(16) {
        enc.encrypt_block_mut(block.into());
    }
    URL_SAFE_NO_PAD.encode(data)
}

struct Fake {
    encrypted: Vec<u8>,
    port: u16,
    /// How many download URLs have been handed out; only the newest one works.
    issued: AtomicUsize,
    expire_first: bool,
}

async fn api(State(f): State<Arc<Fake>>, Json(body): Json<Value>) -> Json<Value> {
    let id = body[0]["p"].as_str().unwrap_or("");
    if id != "clip" {
        return Json(json!([-9]));
    }
    let n = f.issued.fetch_add(1, Ordering::SeqCst) + 1;
    let (key, _) = file_key();
    Json(json!([{
        "s": f.encrypted.len(),
        "at": encrypted_attributes("Clip.mp4", &key),
        "g": format!("http://127.0.0.1:{}/dl/{n}", f.port),
    }]))
}

async fn cdn(State(f): State<Arc<Fake>>, UrlPath((n, range)): UrlPath<(usize, String)>) -> impl IntoResponse {
    let current = f.issued.load(Ordering::SeqCst);
    if n != current || (f.expire_first && n == 1) {
        return (StatusCode::FORBIDDEN, Vec::new());
    }
    let (s, e) = range.split_once('-').unwrap();
    let (s, e): (usize, usize) = (s.parse().unwrap(), e.parse().unwrap());
    (StatusCode::OK, f.encrypted[s..=e.min(f.encrypted.len() - 1)].to_vec())
}

fn start_fake(rt: &tokio::runtime::Runtime, expire_first: bool) -> u16 {
    rt.block_on(async {
        let listener = tokio::net::TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let (key, iv) = file_key();
        let mut encrypted = plain();
        ctr::Ctr128BE::<Aes128>::new(&key.into(), &iv.into()).apply_keystream(&mut encrypted);
        let state = Arc::new(Fake { encrypted, port, issued: AtomicUsize::new(0), expire_first });
        let app = Router::new()
            .route("/cs", post(api))
            .route("/dl/{n}/{range}", get(cdn))
            .with_state(state);
        tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
        port
    })
}

fn engine(port: u16, cache: &Path) -> Engine {
    Engine::start(EngineConfig {
        cache_dir: cache.to_path_buf(),
        enable_dht: false,
        enable_trackers: false,
        enable_upnp: false,
        mega_api: format!("http://127.0.0.1:{port}"),
        ..Default::default()
    })
    .unwrap()
}

fn link() -> String {
    format!("https://mega.nz/file/clip#{}", URL_SAFE_NO_PAD.encode(RAW_KEY))
}

#[test]
fn mega_link_streams_decrypted_with_ranges() {
    let rt = tokio::runtime::Runtime::new().unwrap();
    let port = start_fake(&rt, false);
    let cache = tempfile::tempdir().unwrap();
    let engine = engine(port, cache.path());
    let expected = plain();

    let info = engine.add_mega(&link(), Duration::from_secs(10)).unwrap();
    assert_eq!(info.name, "Clip.mp4");
    assert_eq!(info.size, expected.len() as u64);

    rt.block_on(async {
        let client = reqwest::Client::new();
        let full = client.get(&info.stream_url).send().await.unwrap();
        assert_eq!(full.status(), 200);
        assert_eq!(full.headers()["content-type"], "video/mp4");
        assert!(full.bytes().await.unwrap() == expected, "decrypted bytes must match the original");

        // Odd offsets exercise the CTR block alignment.
        for (s, e) in [(1usize, 16usize), (12_345, 54_321), (expected.len() - 7, expected.len() - 1)] {
            let part = client.get(&info.stream_url).header("Range", format!("bytes={s}-{e}")).send().await.unwrap();
            assert_eq!(part.status(), 206);
            assert_eq!(&part.bytes().await.unwrap()[..], &expected[s..=e], "range {s}-{e}");
        }

        let head = client.head(&info.stream_url).send().await.unwrap();
        assert_eq!(head.headers()["content-length"], expected.len().to_string());
    });
}

#[test]
fn expired_download_urls_are_refreshed() {
    let rt = tokio::runtime::Runtime::new().unwrap();
    let port = start_fake(&rt, true);
    let cache = tempfile::tempdir().unwrap();
    let engine = engine(port, cache.path());
    let info = engine.add_mega(&link(), Duration::from_secs(10)).unwrap();
    rt.block_on(async {
        let body = reqwest::get(&info.stream_url).await.unwrap();
        assert_eq!(body.status(), 200);
        assert_eq!(body.bytes().await.unwrap().len(), plain().len());
    });
}

#[test]
fn missing_files_and_bad_links_give_clear_errors() {
    let rt = tokio::runtime::Runtime::new().unwrap();
    let port = start_fake(&rt, false);
    let cache = tempfile::tempdir().unwrap();
    let engine = engine(port, cache.path());
    let gone = format!("https://mega.nz/file/gone#{}", URL_SAFE_NO_PAD.encode(RAW_KEY));
    let err = engine.add_mega(&gone, Duration::from_secs(10)).unwrap_err();
    assert!(format!("{err:#}").contains("doesn't exist"), "{err:#}");
    let err = engine.add_mega("https://mega.nz/file/clip", Duration::from_secs(10)).unwrap_err();
    assert!(format!("{err:#}").contains("no decryption key"), "{err:#}");
}

#[test]
fn mpv_plays_a_mega_stream() {
    if Command::new("mpv").arg("--version").output().is_err() {
        eprintln!("skipping: mpv not installed");
        return;
    }
    let rt = tokio::runtime::Runtime::new().unwrap();
    let port = start_fake(&rt, false);
    let cache = tempfile::tempdir().unwrap();
    let engine = engine(port, cache.path());
    let info = engine.add_mega(&link(), Duration::from_secs(10)).unwrap();
    let out = Command::new("mpv")
        .args(["--no-config", "--vo=null", "--ao=null", "--frames=20", "--start=1"])
        .arg(&info.stream_url)
        .output()
        .unwrap();
    assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
    assert!(String::from_utf8_lossy(&out.stdout).contains("h264"));
}
