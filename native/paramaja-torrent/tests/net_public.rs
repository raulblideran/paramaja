//! Real-network test against a legal, well-seeded torrent (Blender's *Sintel*, CC-BY).
//! Ignored by default: `cargo test --test net_public -- --ignored`.

use std::{process::Command, time::Duration};

use paramaja_torrent::{Engine, EngineConfig};

const SINTEL: &str = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel\
&tr=udp%3A%2F%2Fexplodie.org%3A6969&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce\
&tr=wss%3A%2F%2Ftracker.btorrent.xyz&tr=wss%3A%2F%2Ftracker.openwebtorrent.com\
&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F";

#[test]
#[ignore = "needs internet"]
fn sintel_streams_from_the_swarm() {
    let cache = tempfile::tempdir().unwrap();
    let engine = Engine::start(EngineConfig { cache_dir: cache.path().into(), ..Default::default() }).unwrap();
    let info = engine.add(SINTEL, Duration::from_secs(90)).expect("metadata should resolve");
    let selected = info.selected_file.unwrap();
    assert!(info.files[selected].path.ends_with("Sintel.mp4"), "{:?}", info.files);

    // Seek to the middle and play a few seconds, as a user would.
    let out = Command::new("mpv")
        .args(["--no-config", "--vo=null", "--ao=null", "--start=50%", "--length=3", "--network-timeout=120"])
        .arg(info.stream_url.unwrap())
        .output()
        .expect("mpv must be installed");
    assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));

    let stats = engine.stats(info.id).unwrap();
    println!("stats after playback: {stats:?}");
    assert!(stats.progress_bytes > 0);
    // Only the selected file should have been fetched (apart from boundary pieces).
    assert!(stats.progress_bytes < info.files[selected].len);
}
