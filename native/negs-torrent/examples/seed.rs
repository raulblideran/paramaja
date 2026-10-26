//! Seeds a file or directory on localhost and prints a magnet link pointing at itself.
//! Used by the C# integration tests (and handy for manual testing):
//!
//!     cargo run --example seed -- path/to/video.mkv
//!
//! Prints `MAGNET <uri>` once ready, then seeds until stdin is closed.

use std::{io::Read, net::Ipv4Addr, path::PathBuf, time::Duration};

use librqbit::{CreateTorrentOptions, ListenerOptions, Session, SessionOptions};

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let path: PathBuf = std::env::args().nth(1).expect("usage: seed <path>").into();
    let path = path.canonicalize()?;
    let parent = path.parent().unwrap().to_path_buf();
    let session = Session::new_with_opts(
        parent,
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
    .await?;
    let (torrent, handle) = session
        .create_and_serve_torrent(&path, CreateTorrentOptions { piece_length: Some(65536), ..Default::default() })
        .await?;
    tokio::time::timeout(Duration::from_secs(60), handle.wait_until_completed()).await??;
    let port = session.listen_addr().unwrap().port();
    println!(
        "MAGNET magnet:?xt=urn:btih:{}&dn={}&x.pe=127.0.0.1:{port}",
        torrent.info_hash().as_string(),
        path.file_name().unwrap().to_string_lossy()
    );
    tokio::task::spawn_blocking(|| {
        let _ = std::io::stdin().read_to_end(&mut Vec::new());
    })
    .await?;
    Ok(())
}
