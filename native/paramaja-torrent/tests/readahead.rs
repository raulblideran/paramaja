//! The stream lookahead is a process-wide setting in the patched librqbit, so this test
//! lives in its own binary where no other engine can change it underneath.

use paramaja_torrent::{Engine, EngineConfig};

#[test]
fn stream_readahead_reaches_librqbit() {
    assert_eq!(EngineConfig::default().stream_readahead_mb, 256);
    let cache_dir = tempfile::tempdir().unwrap();
    let _engine = Engine::start(EngineConfig {
        cache_dir: cache_dir.path().to_path_buf(),
        enable_dht: false,
        enable_trackers: false,
        enable_upnp: false,
        stream_readahead_mb: 512,
        ..Default::default()
    })
    .unwrap();
    assert_eq!(librqbit::stream_readahead(), 512 * 1024 * 1024);
    // Never below librqbit's own 32 MB, whatever the config says.
    librqbit::set_stream_readahead(1);
    assert_eq!(librqbit::stream_readahead(), 32 * 1024 * 1024);
}
