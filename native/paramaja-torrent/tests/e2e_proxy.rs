//! Direct links read ahead (MEGA's desktop app): a fake range server on localhost, throttled
//! per connection as the MEGA app is (about 4-5 MB/s), stands in for it. The engine must serve
//! the same bytes for any range, faster than one connection, and refuse servers without ranges.

use std::{
    net::Ipv4Addr,
    path::Path,
    process::Command,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    },
    time::{Duration, Instant},
};

use axum::{
    Router,
    body::Body,
    extract::State,
    http::{HeaderMap, Method, StatusCode, header},
    response::Response,
    routing::get,
};
use paramaja_torrent::{Engine, EngineConfig};

/// Bytes per second per connection.
const RATE: usize = 2 * 1024 * 1024;

struct Fake {
    data: Vec<u8>,
    ranges: bool,
    open: AtomicUsize,
    most: AtomicUsize,
}

async fn serve(State(f): State<Arc<Fake>>, method: Method, headers: HeaderMap) -> Response {
    let len = f.data.len();
    let builder = Response::builder()
        .header(header::CONTENT_TYPE, "video/x-matroska")
        .header(header::CONNECTION, "close");
    let builder = if f.ranges { builder.header(header::ACCEPT_RANGES, "bytes") } else { builder };
    let range = headers
        .get(header::RANGE)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.strip_prefix("bytes="))
        .and_then(|v| v.split_once('-'))
        .map(|(a, b)| (a.parse::<usize>().unwrap(), if b.is_empty() { len - 1 } else { b.parse::<usize>().unwrap() }));
    let (start, end, status) = match range {
        Some((a, b)) if f.ranges => (a, b.min(len - 1), StatusCode::PARTIAL_CONTENT),
        _ => (0, len - 1, StatusCode::OK),
    };
    let builder = builder.status(status).header(header::CONTENT_LENGTH, end - start + 1);
    let builder = if status == StatusCode::PARTIAL_CONTENT {
        builder.header(header::CONTENT_RANGE, format!("bytes {start}-{end}/{len}"))
    } else {
        builder
    };
    if method == Method::HEAD {
        return builder.body(Body::empty()).unwrap();
    }
    let now = f.open.fetch_add(1, Ordering::SeqCst) + 1;
    f.most.fetch_max(now, Ordering::SeqCst);
    let f2 = f.clone();
    let body = async_stream(f2, start, end);
    builder.body(Body::from_stream(body)).unwrap()
}

/// The slice in 64 KiB pieces at RATE per connection; the open-connection count drops when done
/// or dropped.
fn async_stream(f: Arc<Fake>, start: usize, end: usize) -> impl futures::Stream<Item = Result<Vec<u8>, std::io::Error>> {
    struct Open(Arc<Fake>);
    impl Drop for Open {
        fn drop(&mut self) {
            self.0.open.fetch_sub(1, Ordering::SeqCst);
        }
    }
    const PIECE: usize = 64 * 1024;
    let open = Open(f);
    futures::stream::unfold((start, open), move |(at, open)| async move {
        if at > end {
            return None;
        }
        let last = (at + PIECE - 1).min(end);
        tokio::time::sleep(Duration::from_secs_f64(PIECE as f64 / RATE as f64)).await;
        let piece = open.0.data[at..=last].to_vec();
        Some((Ok(piece), (last + 1, open)))
    })
}

fn pattern(len: usize) -> Vec<u8> {
    (0..len).map(|i| (i * 131 % 253) as u8).collect()
}

fn start_fake(rt: &tokio::runtime::Runtime, data: Vec<u8>, ranges: bool) -> (u16, Arc<Fake>) {
    rt.block_on(async {
        let listener = tokio::net::TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let fake = Arc::new(Fake {
            data,
            ranges,
            open: AtomicUsize::new(0),
            most: AtomicUsize::new(0),
        });
        let app = Router::new().route("/{handle}/{name}", get(serve).head(serve)).with_state(fake.clone());
        tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
        (port, fake)
    })
}

fn engine(cache: &Path) -> Engine {
    Engine::start(EngineConfig {
        cache_dir: cache.to_path_buf(),
        enable_dht: false,
        enable_trackers: false,
        enable_upnp: false,
        ..Default::default()
    })
    .unwrap()
}

#[test]
fn a_direct_link_is_served_in_order_and_faster_than_one_connection() {
    let rt = tokio::runtime::Runtime::new().unwrap();
    let (port, fake) = start_fake(&rt, pattern(40 * 1024 * 1024), true);
    let cache = tempfile::tempdir().unwrap();
    let engine = engine(cache.path());
    let info = engine
        .add_proxy(&format!("http://127.0.0.1:{port}/HEpEyLrD/Michael%20%282026%29.mkv"), Duration::from_secs(10))
        .unwrap();
    assert_eq!(info.name, "Michael (2026).mkv");
    assert_eq!(info.size, fake.data.len() as u64);
    assert!(info.stream_url.contains("/proxy/"), "{}", info.stream_url);

    rt.block_on(async {
        let client = reqwest::Client::new();
        let head = client.head(&info.stream_url).send().await.unwrap();
        assert_eq!(head.headers()["content-length"], fake.data.len().to_string());
        assert_eq!(head.headers()["accept-ranges"], "bytes");

        // 24 MiB from the middle: one connection at RATE would take 12 s.
        let (s, e) = (5_000_001usize, 5_000_001 + 24 * 1024 * 1024 - 1);
        let started = Instant::now();
        let part = client.get(&info.stream_url).header("Range", format!("bytes={s}-{e}")).send().await.unwrap();
        assert_eq!(part.status(), 206);
        assert_eq!(part.headers()["content-range"], format!("bytes {s}-{e}/{}", fake.data.len()));
        let got = part.bytes().await.unwrap();
        let took = started.elapsed();
        assert!(got[..] == fake.data[s..=e], "bytes must match");
        let one_connection = Duration::from_secs_f64((e - s + 1) as f64 / RATE as f64);
        assert!(took < one_connection / 2, "{took:?} vs {one_connection:?} on one connection");
        let most = fake.most.load(Ordering::SeqCst);
        assert!((2..=7).contains(&most), "{most} connections at once");

        // Small and edge ranges, as a player's seeks make them.
        let len = fake.data.len();
        for (s, e) in [(0usize, 0usize), (1, 16), (len - 7, len - 1)] {
            let part = client.get(&info.stream_url).header("Range", format!("bytes={s}-{e}")).send().await.unwrap();
            assert_eq!(&part.bytes().await.unwrap()[..], &fake.data[s..=e], "range {s}-{e}");
        }

        // A player that stops reading (seeks) closes its connection: the read-ahead stops too.
        let mut open = client.get(&info.stream_url).send().await.unwrap();
        open.chunk().await.unwrap();
        drop(open);
        tokio::time::sleep(Duration::from_millis(500)).await;
        assert_eq!(fake.open.load(Ordering::SeqCst), 0, "chunk downloads cancelled");
    });
}

#[test]
fn a_server_without_ranges_is_refused() {
    let rt = tokio::runtime::Runtime::new().unwrap();
    let (port, _) = start_fake(&rt, pattern(1024), false);
    let cache = tempfile::tempdir().unwrap();
    let engine = engine(cache.path());
    let err = engine.add_proxy(&format!("http://127.0.0.1:{port}/a/b.mkv"), Duration::from_secs(10)).unwrap_err();
    assert!(format!("{err:#}").contains("byte ranges"), "{err:#}");
    let err = engine.add_proxy("http://127.0.0.1:1/a/b.mkv", Duration::from_secs(10)).unwrap_err();
    assert!(format!("{err:#}").contains("didn't answer"), "{err:#}");
}

#[test]
fn mpv_plays_a_link_through_the_read_ahead() {
    if Command::new("mpv").arg("--version").output().is_err() {
        eprintln!("skipping: mpv not installed");
        return;
    }
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/fixtures/media/sdr_h264.mp4");
    let Ok(data) = std::fs::read(&fixture) else {
        eprintln!("skipping: run tests/fixtures/make-fixtures.sh first");
        return;
    };
    let rt = tokio::runtime::Runtime::new().unwrap();
    let (port, _) = start_fake(&rt, data, true);
    let cache = tempfile::tempdir().unwrap();
    let engine = engine(cache.path());
    let info = engine.add_proxy(&format!("http://127.0.0.1:{port}/h/clip.mp4"), Duration::from_secs(10)).unwrap();
    let out = Command::new("mpv")
        .args(["--no-config", "--vo=null", "--ao=null", "--frames=20", "--start=1"])
        .arg(&info.stream_url)
        .output()
        .unwrap();
    assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
    assert!(String::from_utf8_lossy(&out.stdout).contains("h264"));
}

/// A real link (MEGA's desktop app): `PARAMAJA_PROXY_URL=http://127.0.0.1:4443/… cargo test --test
/// e2e_proxy -- --ignored --nocapture`, optionally `PARAMAJA_PROXY_AT=<byte offset>` to read a
/// part of the file the app hasn't fetched yet. Prints the rate through the read-ahead.
#[test]
#[ignore]
fn real_link_rate() {
    let Ok(url) = std::env::var("PARAMAJA_PROXY_URL") else {
        eprintln!("set PARAMAJA_PROXY_URL");
        return;
    };
    let at: u64 = std::env::var("PARAMAJA_PROXY_AT").ok().and_then(|v| v.parse().ok()).unwrap_or(0);
    let rt = tokio::runtime::Runtime::new().unwrap();
    let cache = tempfile::tempdir().unwrap();
    let engine = engine(cache.path());
    let info = engine.add_proxy(&url, Duration::from_secs(20)).unwrap();
    println!("{} ({} bytes) at {}", info.name, info.size, info.stream_url);
    rt.block_on(async {
        let client = reqwest::Client::new();
        let mut r = client.get(&info.stream_url).header("Range", format!("bytes={at}-")).send().await.unwrap();
        if r.status() != 206 {
            panic!("HTTP {}: {}", r.status(), r.text().await.unwrap());
        }
        let started = Instant::now();
        let (mut got, mut first) = (0u64, None);
        while got < 200 * 1024 * 1024 {
            let b = match r.chunk().await {
                Ok(Some(b)) => b,
                Ok(None) => { println!("body ended after {got} bytes"); break }
                Err(e) => { println!("body failed after {got} bytes: {e:#?}"); break }
            };
            first.get_or_insert_with(|| started.elapsed());
            got += b.len() as u64;
        }
        let took = started.elapsed().as_secs_f64();
        println!("first bytes after {:?}; {} MiB in {took:.1} s = {:.1} MiB/s", first.unwrap(), got >> 20, got as f64 / took / 1048576.0);
    });
}

/// Seeks against a real link: ten requests dropped after a few MiB each (their chunk downloads
/// cut mid-way), then one more must still read at speed. Same variables as `real_link_rate`.
#[test]
#[ignore]
fn real_link_survives_seeks() {
    let Ok(url) = std::env::var("PARAMAJA_PROXY_URL") else {
        eprintln!("set PARAMAJA_PROXY_URL");
        return;
    };
    let at: u64 = std::env::var("PARAMAJA_PROXY_AT").ok().and_then(|v| v.parse().ok()).unwrap_or(0);
    let rt = tokio::runtime::Runtime::new().unwrap();
    let cache = tempfile::tempdir().unwrap();
    let engine = engine(cache.path());
    let info = engine.add_proxy(&url, Duration::from_secs(20)).unwrap();
    rt.block_on(async {
        let client = reqwest::Client::new();
        for k in 0..10u64 {
            let from = at + k * 700_000_000;
            let mut r = client.get(&info.stream_url).header("Range", format!("bytes={from}-")).send().await.unwrap();
            assert_eq!(r.status(), 206, "seek {k}");
            let mut got = 0;
            while got < 3 << 20 {
                got += r.chunk().await.unwrap().unwrap().len();
            }
            drop(r);
        }
        let started = Instant::now();
        let mut r = client.get(&info.stream_url).header("Range", format!("bytes={}-", at + 7_500_000_000)).send().await.unwrap();
        assert_eq!(r.status(), 206);
        let mut got = 0u64;
        while got < 100 << 20 {
            got += r.chunk().await.unwrap().unwrap().len() as u64;
        }
        println!("after 10 seeks: {:.1} MiB/s", got as f64 / started.elapsed().as_secs_f64() / 1048576.0);
    });
}
