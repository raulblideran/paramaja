//! Offline end-to-end test: a local seeder serves a small torrent, the engine adds it by
//! magnet (pointing at the seeder via `x.pe`) and we stream it over HTTP like mpv would.

use std::{
    net::Ipv4Addr,
    path::{Path, PathBuf},
    process::Command,
    sync::Arc,
    time::Duration,
};

use librqbit::{CreateTorrentOptions, ListenerOptions, Session, SessionOptions};
use paramaja_torrent::{Engine, EngineConfig};
use tokio::runtime::Runtime;

struct Seeder {
    rt: Runtime,
    _session: Arc<Session>,
    magnet: String,
}

fn fixture(name: &str) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tests/fixtures/media").join(name)
}

fn movie_bytes() -> Vec<u8> {
    // Prefer a real clip so mpv can play it; fall back to pseudo-random bytes.
    std::fs::read(fixture("sdr_h264.mp4")).unwrap_or_else(|_| {
        (0..400_000u32).map(|i| (i.wrapping_mul(2654435761) >> 13) as u8).collect()
    })
}

fn start_seeder(dir: &Path) -> Seeder {
    let content = dir.join("content");
    std::fs::create_dir_all(content.join("Sample")).unwrap();
    std::fs::write(content.join("movie.mp4"), movie_bytes()).unwrap();
    std::fs::write(content.join("Sample/movie-sample.mkv"), vec![7u8; 20_000]).unwrap();
    std::fs::write(content.join("readme.txt"), b"legal test content").unwrap();

    let rt = tokio::runtime::Builder::new_multi_thread().enable_all().build().unwrap();
    let (session, magnet) = rt.block_on(async {
        let session = Session::new_with_opts(
            dir.to_path_buf(),
            SessionOptions {
                dht: None,
                disable_trackers: true,
                disable_local_service_discovery: true,
                persistence: None,
                listen: Some(ListenerOptions {
                    listen_addr: (Ipv4Addr::LOCALHOST, 0).into(),
                    ..Default::default()
                }),
                ..Default::default()
            },
        )
        .await
        .unwrap();
        let (torrent, handle) = session
            .create_and_serve_torrent(
                &content,
                CreateTorrentOptions { name: Some("paramaja-test"), piece_length: Some(16384), ..Default::default() },
            )
            .await
            .unwrap();
        tokio::time::timeout(Duration::from_secs(20), handle.wait_until_completed())
            .await
            .expect("seeder should finish hashing")
            .unwrap();
        let peer = session.listen_addr().unwrap();
        let magnet = format!(
            "magnet:?xt=urn:btih:{}&dn=paramaja-test&x.pe=127.0.0.1:{}",
            torrent.info_hash().as_string(),
            peer.port()
        );
        (session, magnet)
    });
    Seeder { rt, _session: session, magnet }
}

fn offline_engine(cache: &Path) -> Engine {
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
fn magnet_resolves_selects_largest_video_and_streams_with_ranges() {
    let seed_dir = tempfile::tempdir().unwrap();
    let cache_dir = tempfile::tempdir().unwrap();
    let seeder = start_seeder(seed_dir.path());
    let engine = offline_engine(cache_dir.path());
    let expected = movie_bytes();

    let info = engine.add(&seeder.magnet, Duration::from_secs(30)).expect("add magnet");
    assert_eq!(info.name.as_deref(), Some("paramaja-test"));
    assert_eq!(info.files.len(), 3);
    let movie = info.files.iter().find(|f| f.path.ends_with("movie.mp4")).unwrap();
    assert_eq!(info.selected_file, Some(movie.index), "largest non-sample video is chosen");
    let url = info.stream_url.clone().unwrap();
    assert!(url.starts_with("http://127.0.0.1:"));

    seeder.rt.block_on(async {
        let client = reqwest::Client::new();

        let full = client.get(&url).send().await.unwrap();
        assert_eq!(full.status(), 200);
        assert_eq!(full.headers()["accept-ranges"], "bytes");
        assert_eq!(full.headers()["content-type"], "video/mp4");
        let body = full.bytes().await.unwrap();
        assert_eq!(body.len(), expected.len());
        assert!(body == expected, "streamed bytes must match the source");

        // A mid-file seek, as mpv issues when the user jumps ahead.
        let mid = expected.len() as u64 / 2;
        let part = client.get(&url).header("Range", format!("bytes={mid}-{}", mid + 999)).send().await.unwrap();
        assert_eq!(part.status(), 206);
        assert_eq!(part.headers()["content-range"], format!("bytes {mid}-{}/{}", mid + 999, expected.len()));
        assert_eq!(&part.bytes().await.unwrap()[..], &expected[mid as usize..mid as usize + 1000]);

        let tail = client.get(&url).header("Range", "bytes=-100").send().await.unwrap();
        assert_eq!(tail.status(), 206);
        assert_eq!(&tail.bytes().await.unwrap()[..], &expected[expected.len() - 100..]);

        let head = client.head(&url).send().await.unwrap();
        assert_eq!(head.headers()["content-length"], expected.len().to_string());

        let bad = client.get(&url).header("Range", format!("bytes={}-", expected.len())).send().await.unwrap();
        assert_eq!(bad.status(), 416);

        let missing = client.get(url.replace("/stream/", "/stream/999")).send().await.unwrap();
        assert_eq!(missing.status(), 404);
    });

    let stats = engine.stats(info.id).unwrap();
    assert!(stats.file_progress[movie.index] >= expected.len() as u64);
    assert_eq!(stats.file_progress.len(), 3);

    // Switching the selected file returns that file's stream URL.
    let readme = info.files.iter().find(|f| f.path.ends_with("readme.txt")).unwrap();
    let readme_url = engine.select_file(info.id, readme.index).unwrap();
    assert!(readme_url.ends_with(&format!("/{}", readme.index)));
    assert!(engine.select_file(info.id, 99).is_err());

    // Adding the same magnet again returns the already managed torrent.
    let again = engine.add(&seeder.magnet, Duration::from_secs(10)).unwrap();
    assert_eq!(again.id, info.id);

    engine.remove(info.id, true).unwrap();
    assert!(engine.stats(info.id).is_err());
}

#[test]
fn mpv_plays_the_torrent_stream() {
    if Command::new("mpv").arg("--version").output().is_err() || !fixture("sdr_h264.mp4").exists() {
        eprintln!("skipping: mpv or fixtures not available");
        return;
    }
    let seed_dir = tempfile::tempdir().unwrap();
    let cache_dir = tempfile::tempdir().unwrap();
    let seeder = start_seeder(seed_dir.path());
    let engine = offline_engine(cache_dir.path());
    let info = engine.add(&seeder.magnet, Duration::from_secs(30)).unwrap();

    let out = Command::new("mpv")
        .args(["--no-config", "--vo=null", "--ao=null", "--frames=30", "--msg-level=all=info"])
        .arg(info.stream_url.unwrap())
        .output()
        .unwrap();
    let log = String::from_utf8_lossy(&out.stdout);
    assert!(out.status.success(), "mpv failed: {log}{}", String::from_utf8_lossy(&out.stderr));
    assert!(log.contains("h264"), "mpv should detect the video stream: {log}");
}

#[test]
fn invalid_magnet_is_rejected_quickly() {
    let cache_dir = tempfile::tempdir().unwrap();
    let engine = offline_engine(cache_dir.path());
    let err = engine.add("not a magnet", Duration::from_secs(1)).unwrap_err();
    assert!(err.to_string().contains("not a magnet"));
}

#[test]
fn unreachable_magnet_times_out() {
    let cache_dir = tempfile::tempdir().unwrap();
    let engine = offline_engine(cache_dir.path());
    let err = engine
        .add("magnet:?xt=urn:btih:0000000000000000000000000000000000000001", Duration::from_millis(800))
        .unwrap_err();
    assert!(format!("{err:#}").contains("timed out"), "{err:#}");
}
