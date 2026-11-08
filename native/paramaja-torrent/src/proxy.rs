//! Direct HTTP links read through the parallel read-ahead (see `parallel`).
//!
//! MEGA's desktop app streams a cloud file at `http://127.0.0.1:4443/<handle>/<name>`, at about
//! 4-5 MB/s per connection: less than a 4K remux needs in its busy scenes. Served from here,
//! the player reads one connection while the engine reads the file several chunks at once.

use std::{
    collections::HashMap,
    sync::{
        Arc, Mutex,
        atomic::{AtomicUsize, Ordering},
    },
    time::Duration,
};

use anyhow::{Context, bail};
use bytes::Bytes;
use reqwest::{StatusCode, header};
use serde::Serialize;

use crate::parallel::{self, FetchError, RangeSource};

#[derive(Debug, Clone, Serialize)]
pub struct ProxyInfo {
    pub id: usize,
    pub name: String,
    pub size: u64,
    pub stream_url: String,
}

pub struct ProxyEntry {
    pub url: String,
    pub size: u64,
    pub name: String,
}

pub struct ProxyRegistry {
    client: reqwest::Client,
    next: AtomicUsize,
    entries: Mutex<HashMap<usize, Arc<ProxyEntry>>>,
}

impl Default for ProxyRegistry {
    fn default() -> Self {
        Self {
            // MEGA's desktop app reads "Range:" only in that case: hyper's lowercase "range:"
            // gets the whole file back, with a 200.
            client: reqwest::Client::builder()
                .user_agent("Paramaja")
                .http1_title_case_headers()
                .connect_timeout(Duration::from_secs(15))
                .build()
                .expect("http client"),
            next: AtomicUsize::new(0),
            entries: Mutex::new(HashMap::new()),
        }
    }
}

/// The last path segment, percent-decoded: the MEGA app's links end in the file's name.
pub fn name_of(url: &url::Url) -> String {
    url.path_segments()
        .and_then(|mut s| s.next_back())
        .filter(|s| !s.is_empty())
        .map(|s| percent_encoding::percent_decode_str(s).decode_utf8_lossy().into_owned())
        .unwrap_or_else(|| "stream".to_owned())
}

impl ProxyRegistry {
    /// Checks the link answers byte ranges (a HEAD request) and registers it. Anything that
    /// doesn't is an error: the app then plays the link directly.
    pub async fn add(&self, input: &str) -> anyhow::Result<(usize, Arc<ProxyEntry>)> {
        let url = url::Url::parse(input.trim()).context("not a valid link")?;
        if !matches!(url.scheme(), "http" | "https") {
            bail!("only http(s) links can be read ahead");
        }
        let head = self.client.head(url.as_str()).send().await.context("the link didn't answer")?;
        if !head.status().is_success() {
            bail!("the link answered HTTP {}", head.status().as_u16());
        }
        let ranges = head
            .headers()
            .get(header::ACCEPT_RANGES)
            .and_then(|v| v.to_str().ok())
            .is_some_and(|v| v.eq_ignore_ascii_case("bytes"));
        let size = head
            .headers()
            .get(header::CONTENT_LENGTH)
            .and_then(|v| v.to_str().ok())
            .and_then(|v| v.parse::<u64>().ok())
            .unwrap_or(0);
        if !ranges || size == 0 {
            bail!("the link doesn't serve byte ranges");
        }
        // Saying so isn't enough: ask for one byte. (Dropping a 200 closes its connection.)
        let probe = self
            .client
            .get(url.as_str())
            .header(header::RANGE, "bytes=0-0")
            .send()
            .await
            .context("the link didn't answer")?;
        if probe.status() != StatusCode::PARTIAL_CONTENT {
            bail!("the link doesn't serve byte ranges (HTTP {} for one byte)", probe.status().as_u16());
        }
        drop(probe);
        let entry = Arc::new(ProxyEntry { name: name_of(&url), url: url.into(), size });
        let id = self.next.fetch_add(1, Ordering::Relaxed);
        self.entries.lock().unwrap().insert(id, entry.clone());
        Ok((id, entry))
    }

    pub fn get(&self, id: usize) -> Option<Arc<ProxyEntry>> {
        self.entries.lock().unwrap().get(&id).cloned()
    }

    pub fn source(&self, entry: Arc<ProxyEntry>) -> DirectSource {
        DirectSource { client: self.client.clone(), entry }
    }
}

pub struct DirectSource {
    client: reqwest::Client,
    entry: Arc<ProxyEntry>,
}

impl RangeSource for DirectSource {
    async fn fetch(&self, start: u64, end: u64) -> Result<Bytes, FetchError> {
        let response = self
            .client
            .get(&self.entry.url)
            .header(header::RANGE, format!("bytes={start}-{end}"))
            .send()
            .await
            .map_err(|e| FetchError::Retry(e.into()))?;
        let whole = start == 0 && end + 1 == self.entry.size;
        match response.status() {
            StatusCode::PARTIAL_CONTENT => {}
            StatusCode::OK if whole => {}
            StatusCode::OK => {
                return Err(FetchError::Fatal(anyhow::anyhow!("the link ignored the byte range")));
            }
            s if s.is_server_error() || s == StatusCode::TOO_MANY_REQUESTS => {
                return Err(FetchError::Retry(anyhow::anyhow!("the link answered HTTP {}", s.as_u16())));
            }
            s => return Err(FetchError::Fatal(anyhow::anyhow!("the link answered HTTP {}", s.as_u16()))),
        }
        parallel::read_all(response, end - start + 1).await
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_come_from_the_last_path_segment() {
        let u = url::Url::parse(
            "http://127.0.0.1:4443/HEpEyLrD/Michael%20%282026%29%202160p%20DV%20HDR%20H.265-BYNDR.mkv",
        )
        .unwrap();
        assert_eq!(name_of(&u), "Michael (2026) 2160p DV HDR H.265-BYNDR.mkv");
        assert_eq!(name_of(&url::Url::parse("http://h/").unwrap()), "stream");
    }
}
