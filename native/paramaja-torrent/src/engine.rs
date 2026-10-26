//! The streaming engine: a librqbit session plus a localhost HTTP server that
//! serves torrent files with Range support so mpv can play (and seek) them.

use std::{
    collections::HashSet,
    net::{Ipv4Addr, SocketAddr},
    path::PathBuf,
    sync::Arc,
    time::Duration,
};

use anyhow::{Context, bail};
use librqbit::{
    AddTorrent, AddTorrentOptions, ListenerOptions, ManagedTorrent, Session, SessionOptions,
    api::TorrentIdOrHash,
};

type ManagedTorrentHandle = Arc<ManagedTorrent>;
use serde::{Deserialize, Serialize};
use tokio::runtime::Runtime;

use crate::{
    http, magnet, mega,
    selection::{self, FileEntry},
};

#[derive(Debug, Clone, Deserialize)]
#[serde(default)]
pub struct EngineConfig {
    /// Where downloaded data lives (the portable app passes `<exe dir>/cache`).
    pub cache_dir: PathBuf,
    /// Port for the localhost stream server; 0 picks a free one.
    pub http_port: u16,
    /// Port for incoming BitTorrent connections; 0 picks a free one.
    pub listen_port: u16,
    pub enable_dht: bool,
    pub enable_trackers: bool,
    pub enable_upnp: bool,
    /// How far ahead of the playhead pieces are fetched, in order. 4K remuxes run at
    /// 60-100 Mbit/s, so librqbit's 32 MB default is only about three seconds of film.
    pub stream_readahead_mb: u64,
    /// MEGA API endpoint (overridden by tests).
    pub mega_api: String,
}

impl Default for EngineConfig {
    fn default() -> Self {
        Self {
            cache_dir: PathBuf::from("cache"),
            http_port: 0,
            listen_port: 0,
            enable_dht: true,
            enable_trackers: true,
            enable_upnp: true,
            stream_readahead_mb: 256,
            mega_api: mega::DEFAULT_API.to_owned(),
        }
    }
}

#[derive(Debug, Clone, Serialize)]
pub struct TorrentInfo {
    pub id: usize,
    pub info_hash: String,
    pub name: Option<String>,
    pub files: Vec<FileEntry>,
    pub selected_file: Option<usize>,
    pub stream_url: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
pub struct TorrentStatsDto {
    pub state: String,
    pub error: Option<String>,
    pub finished: bool,
    pub progress_bytes: u64,
    pub total_bytes: u64,
    pub file_progress: Vec<u64>,
    pub download_mbps: f64,
    pub upload_mbps: f64,
    pub peers_live: u32,
    pub peers_connecting: u32,
    pub peers_seen: u32,
}

pub struct Engine {
    rt: Option<Runtime>,
    session: Arc<Session>,
    http_addr: SocketAddr,
    cache_dir: PathBuf,
    mega: Arc<mega::MegaRegistry>,
}

impl Engine {
    pub fn start(config: EngineConfig) -> anyhow::Result<Self> {
        let rt = tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .thread_name("paramaja-torrent")
            .build()?;
        std::fs::create_dir_all(&config.cache_dir)
            .with_context(|| format!("cannot create cache dir {:?}", config.cache_dir))?;
        let cache_dir = config.cache_dir.canonicalize()?;
        librqbit::set_stream_readahead(config.stream_readahead_mb * 1024 * 1024);

        let mega = Arc::new(mega::MegaRegistry::new(config.mega_api.clone()));
        let (session, http_addr) = rt.block_on(async {
            let session = Session::new_with_opts(
                cache_dir.clone(),
                SessionOptions {
                    dht: config.enable_dht.then(|| librqbit::DhtSessionConfig {
                        persistence: None,
                        ..Default::default()
                    }),
                    disable_trackers: !config.enable_trackers,
                    persistence: None,
                    fastresume: false,
                    listen: Some(ListenerOptions {
                        listen_addr: (Ipv4Addr::UNSPECIFIED, config.listen_port).into(),
                        enable_upnp_port_forwarding: config.enable_upnp,
                        ..Default::default()
                    }),
                    ..Default::default()
                },
            )
            .await
            .context("failed to start torrent session")?;
            let state = http::AppState { session: session.clone(), mega: mega.clone() };
            let http_addr = http::serve(state, config.http_port).await?;
            anyhow::Ok((session, http_addr))
        })?;

        Ok(Self { rt: Some(rt), session, http_addr, cache_dir, mega })
    }

    pub fn http_addr(&self) -> SocketAddr {
        self.http_addr
    }

    pub fn listen_addr(&self) -> Option<SocketAddr> {
        self.session.listen_addr()
    }

    pub fn stream_url(&self, id: usize, file: usize) -> String {
        format!("http://{}/stream/{id}/{file}", self.http_addr)
    }

    /// Resolves the magnet's metadata, selects the default video file and starts downloading it.
    pub fn add(&self, input: &str, timeout: Duration) -> anyhow::Result<TorrentInfo> {
        let parsed = magnet::parse(input)?;
        let output_folder = self.cache_dir.join(&parsed.info_hash);
        let handle = self.rt().block_on(async {
            let resp = tokio::time::timeout(
                timeout,
                self.session.add_torrent(
                    AddTorrent::from_url(parsed.uri.clone()),
                    Some(AddTorrentOptions {
                        // Start paused so nothing but the chosen file gets downloaded.
                        paused: true,
                        overwrite: true,
                        output_folder: Some(output_folder.to_string_lossy().into_owned()),
                        initial_peers: (!parsed.peers.is_empty()).then(|| parsed.peers.clone()),
                        ..Default::default()
                    }),
                ),
            )
            .await
            .context("timed out resolving the magnet link (no peers found)")??;
            resp.into_handle().context("torrent was not added")
        })?;

        let files = files_of(&handle)?;
        let selected = match handle.only_files() {
            // Already managed and streaming something: keep that choice.
            Some(only) if !handle.is_paused() && only.len() == 1 => Some(only[0]),
            _ => selection::pick_default(&files),
        };
        if let Some(file) = selected {
            self.select_file_on(&handle, file)?;
        }
        Ok(TorrentInfo {
            id: handle.id(),
            info_hash: parsed.info_hash,
            name: handle.name(),
            files,
            selected_file: selected,
            stream_url: selected.map(|f| self.stream_url(handle.id(), f)),
        })
    }

    /// Resolves a MEGA file link and returns a local URL that streams it decrypted.
    pub fn add_mega(&self, link: &str, timeout: Duration) -> anyhow::Result<mega::MegaInfo> {
        let (id, file) = self.rt().block_on(async {
            tokio::time::timeout(timeout, self.mega.add(link))
                .await
                .context("timed out contacting MEGA")?
        })?;
        Ok(mega::MegaInfo {
            id,
            name: file.name,
            size: file.size,
            stream_url: format!("http://{}/mega/{id}", self.http_addr),
        })
    }

    pub fn select_file(&self, id: usize, file: usize) -> anyhow::Result<String> {
        let handle = self.handle(id)?;
        self.select_file_on(&handle, file)?;
        Ok(self.stream_url(id, file))
    }

    fn select_file_on(&self, handle: &ManagedTorrentHandle, file: usize) -> anyhow::Result<()> {
        let count = handle.with_metadata(|m| m.file_infos.len())?;
        if file >= count {
            bail!("file index {file} out of range (torrent has {count} files)");
        }
        self.rt().block_on(async {
            // Initialisation (checking any cached data) must finish before the selection can change.
            handle.wait_until_initialized().await?;
            self.session.update_only_files(handle, &HashSet::from([file])).await?;
            if handle.is_paused() {
                self.session.unpause(handle).await?;
            }
            anyhow::Ok(())
        })
    }

    pub fn stats(&self, id: usize) -> anyhow::Result<TorrentStatsDto> {
        let s = self.handle(id)?.stats();
        let live = s.live.as_ref();
        let peers = live.map(|l| &l.snapshot.peer_stats);
        Ok(TorrentStatsDto {
            state: s.state.to_string(),
            error: s.error.clone(),
            finished: s.finished,
            progress_bytes: s.progress_bytes,
            total_bytes: s.total_bytes,
            file_progress: s.file_progress.clone(),
            download_mbps: live.map_or(0.0, |l| l.download_speed.mbps),
            upload_mbps: live.map_or(0.0, |l| l.upload_speed.mbps),
            peers_live: peers.map_or(0, |p| p.live),
            peers_connecting: peers.map_or(0, |p| p.connecting),
            peers_seen: peers.map_or(0, |p| p.seen),
        })
    }

    pub fn remove(&self, id: usize, delete_files: bool) -> anyhow::Result<()> {
        self.rt()
            .block_on(self.session.delete(TorrentIdOrHash::Id(id), delete_files))
    }

    fn rt(&self) -> &Runtime {
        self.rt.as_ref().expect("runtime is only taken in Drop")
    }

    fn handle(&self, id: usize) -> anyhow::Result<ManagedTorrentHandle> {
        self.session
            .get(TorrentIdOrHash::Id(id))
            .with_context(|| format!("no torrent with id {id}"))
    }
}

fn files_of(handle: &ManagedTorrentHandle) -> anyhow::Result<Vec<FileEntry>> {
    handle.with_metadata(|m| {
        m.file_infos
            .iter()
            .enumerate()
            .filter(|(_, fi)| !fi.attrs.padding)
            .map(|(index, fi)| {
                let path = fi.relative_filename.to_string_lossy().replace('\\', "/");
                FileEntry { index, is_video: selection::is_video(&path), path, len: fi.len }
            })
            .collect()
    })
}

impl Drop for Engine {
    fn drop(&mut self) {
        if let Some(rt) = self.rt.take() {
            let session = self.session.clone();
            rt.block_on(async {
                let _ = tokio::time::timeout(Duration::from_secs(3), session.stop()).await;
            });
            rt.shutdown_timeout(Duration::from_secs(2));
        }
    }
}
