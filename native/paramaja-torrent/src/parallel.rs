//! Parallel, ordered read-ahead for HTTP sources that are slow per connection.
//!
//! MEGA's desktop app streams at about 4-5 MB/s per connection, and MEGA's CDN limits each
//! connection too, while reads of different byte ranges in parallel add up (six gave
//! 22.8 MiB/s from the desktop app). So a response is fetched as a run of byte-range chunks,
//! several at once, and sent to the player in order.
//!
//! The first chunks are small (512 KiB, then 1, 2 and 4 MiB) so a request answers quickly:
//! opening an MKV seeks to its end and back, and each seek is a new request. Every chunk runs
//! as its own task, so the read-ahead keeps downloading while the player isn't reading, and
//! is aborted when the response is dropped (the player seeked or stopped). At most
//! `PARALLEL` chunks are in memory per response.

use std::{
    future::Future,
    pin::Pin,
    sync::Arc,
    task::{Context, Poll},
    time::Duration,
};

use bytes::Bytes;
use futures::{Stream, StreamExt, stream};

/// Chunks fetched at once, per response.
pub const PARALLEL: usize = 6;
pub const FIRST_CHUNK: u64 = 512 * 1024;
pub const MAX_CHUNK: u64 = 8 * 1024 * 1024;
/// Attempts per chunk before the response fails (the player then reconnects at its position).
pub const ATTEMPTS: u32 = 4;
/// A chunk whose download sends nothing for this long is fetched again.
pub const STALL: Duration = Duration::from_secs(20);

/// The inclusive byte ranges `[start, end]` is fetched in: 512 KiB, 1, 2, 4, then 8 MiB each.
pub fn chunks(start: u64, end: u64) -> impl Iterator<Item = (u64, u64)> + Send + 'static {
    let mut at = start;
    let mut size = FIRST_CHUNK;
    std::iter::from_fn(move || {
        if at > end {
            return None;
        }
        let last = (at + size - 1).min(end);
        let chunk = (at, last);
        at = last + 1;
        size = (size * 2).min(MAX_CHUNK);
        Some(chunk)
    })
}

/// Fetches one inclusive byte range. Errors are retried (see [`ATTEMPTS`]) unless the
/// source says they're final.
pub trait RangeSource: Send + Sync + 'static {
    fn fetch(&self, start: u64, end: u64) -> impl Future<Output = Result<Bytes, FetchError>> + Send;
}

#[derive(Debug)]
pub enum FetchError {
    /// Worth another try (a dropped connection, a stall, a 5xx).
    Retry(anyhow::Error),
    /// Trying again won't help (MEGA's transfer quota, a 4xx).
    Fatal(anyhow::Error),
}

impl std::fmt::Display for FetchError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            FetchError::Retry(e) | FetchError::Fatal(e) => write!(f, "{e:#}"),
        }
    }
}

/// One chunk, retried with a short back-off.
pub async fn fetch_with_retries<S: RangeSource>(source: &S, start: u64, end: u64) -> anyhow::Result<Bytes> {
    let mut last = None;
    for attempt in 0..ATTEMPTS {
        if attempt > 0 {
            tokio::time::sleep(Duration::from_millis(250 << attempt)).await;
        }
        match source.fetch(start, end).await {
            Ok(b) if b.len() as u64 == end - start + 1 => return Ok(b),
            Ok(b) => {
                last = Some(anyhow::anyhow!(
                    "got {} bytes for {start}-{end} ({} expected)",
                    b.len(),
                    end - start + 1
                ))
            }
            Err(FetchError::Fatal(e)) => return Err(e),
            Err(FetchError::Retry(e)) => last = Some(e),
        }
    }
    Err(last.unwrap_or_else(|| anyhow::anyhow!("no attempts")))
}

/// A spawned task that is aborted when dropped.
struct Task(tokio::task::JoinHandle<anyhow::Result<Bytes>>);

impl Drop for Task {
    fn drop(&mut self) {
        self.0.abort();
    }
}

impl Future for Task {
    type Output = anyhow::Result<Bytes>;
    fn poll(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Self::Output> {
        Pin::new(&mut self.0).poll(cx).map(|r| r.unwrap_or_else(|e| Err(anyhow::anyhow!("chunk task failed: {e}"))))
    }
}

/// The bytes `[start, end]` in order, `PARALLEL` chunks ahead. The first chunk is fetched
/// before this returns, so a source that refuses the request (an expired link, a quota) is
/// an error the caller can answer with a status code rather than a cut-off body.
pub async fn body<S: RangeSource>(
    source: Arc<S>,
    start: u64,
    end: u64,
) -> anyhow::Result<impl Stream<Item = Result<Bytes, std::io::Error>> + Send + 'static> {
    let mut ranges = chunks(start, end);
    let (a, b) = ranges.next().expect("a non-empty range has a chunk");
    let first = fetch_with_retries(source.as_ref(), a, b).await?;
    let rest = stream::iter(ranges)
        .map(move |(a, b)| {
            let source = source.clone();
            Task(tokio::spawn(async move { fetch_with_retries(source.as_ref(), a, b).await }))
        })
        .buffered(PARALLEL)
        .map(|r| r.map_err(std::io::Error::other));
    Ok(stream::once(async move { Ok::<_, std::io::Error>(first) }).chain(rest))
}

/// Reads a response body, failing (for a retry) if it stalls for [`STALL`].
pub async fn read_all(mut response: reqwest::Response, expected: u64) -> Result<Bytes, FetchError> {
    let mut buf = bytes::BytesMut::with_capacity(expected as usize);
    loop {
        match tokio::time::timeout(STALL, response.chunk()).await {
            Err(_) => return Err(FetchError::Retry(anyhow::anyhow!("the download stalled"))),
            Ok(Err(e)) => return Err(FetchError::Retry(e.into())),
            Ok(Ok(None)) => return Ok(buf.freeze()),
            Ok(Ok(Some(b))) => buf.extend_from_slice(&b),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{
        Mutex,
        atomic::{AtomicUsize, Ordering},
    };

    #[test]
    fn chunks_grow_to_8_mib_and_cover_the_range_exactly() {
        let all: Vec<_> = chunks(100, 100 + 40 * 1024 * 1024 - 1).collect();
        let sizes: Vec<u64> = all.iter().map(|(a, b)| b - a + 1).collect();
        assert_eq!(&sizes[..5], &[512 << 10, 1 << 20, 2 << 20, 4 << 20, 8 << 20]);
        assert!(sizes.iter().all(|&s| s <= MAX_CHUNK));
        assert_eq!(all[0].0, 100);
        assert_eq!(all.last().unwrap().1, 100 + 40 * 1024 * 1024 - 1);
        assert!(all.windows(2).all(|w| w[1].0 == w[0].1 + 1), "contiguous");
        assert_eq!(chunks(5, 5).collect::<Vec<_>>(), vec![(5, 5)]);
        assert_eq!(chunks(10, 20).collect::<Vec<_>>(), vec![(10, 20)]);
    }

    /// Serves `data` with a delay per chunk that makes later chunks finish first, fails each
    /// chunk's first attempt when asked to, and counts how many fetches run at once.
    struct Fake {
        data: Vec<u8>,
        fail_first: bool,
        attempts: Mutex<std::collections::HashMap<u64, u32>>,
        running: AtomicUsize,
        most: AtomicUsize,
        started: AtomicUsize,
    }

    impl Fake {
        fn new(len: usize, fail_first: bool) -> Arc<Self> {
            Arc::new(Self {
                data: (0..len).map(|i| (i * 31 % 251) as u8).collect(),
                fail_first,
                attempts: Mutex::default(),
                running: AtomicUsize::new(0),
                most: AtomicUsize::new(0),
                started: AtomicUsize::new(0),
            })
        }
    }

    impl RangeSource for Fake {
        async fn fetch(&self, start: u64, end: u64) -> Result<Bytes, FetchError> {
            self.started.fetch_add(1, Ordering::SeqCst);
            let now = self.running.fetch_add(1, Ordering::SeqCst) + 1;
            self.most.fetch_max(now, Ordering::SeqCst);
            // Counted down even when the task is aborted mid-fetch.
            struct Running<'a>(&'a AtomicUsize);
            impl Drop for Running<'_> {
                fn drop(&mut self) {
                    self.0.fetch_sub(1, Ordering::SeqCst);
                }
            }
            let running = Running(&self.running);
            // Earlier chunks take longer, so out-of-order completion is the norm.
            let delay = 30u64.saturating_sub(start / (1 << 20));
            tokio::time::sleep(Duration::from_millis(delay)).await;
            drop(running);
            let n = {
                let mut a = self.attempts.lock().unwrap();
                let n = a.entry(start).or_default();
                *n += 1;
                *n
            };
            if self.fail_first && n == 1 {
                return Err(FetchError::Retry(anyhow::anyhow!("dropped")));
            }
            Ok(Bytes::copy_from_slice(&self.data[start as usize..=end as usize]))
        }
    }

    async fn collect(s: impl Stream<Item = Result<Bytes, std::io::Error>>) -> Vec<u8> {
        let mut out = Vec::new();
        futures::pin_mut!(s);
        while let Some(b) = s.next().await {
            out.extend_from_slice(&b.unwrap());
        }
        out
    }

    #[tokio::test]
    async fn sends_the_bytes_in_order_with_chunks_in_parallel() {
        let fake = Fake::new(30 << 20, false);
        for (a, b) in [(0u64, (30 << 20) - 1), (12345, 20_000_000), (29 << 20, (30 << 20) - 1)] {
            let got = collect(body(fake.clone(), a, b).await.unwrap()).await;
            assert_eq!(got, &fake.data[a as usize..=b as usize], "{a}-{b}");
        }
        let most = fake.most.load(Ordering::SeqCst);
        assert!(most > 1 && most <= PARALLEL + 1, "{most} at once");
    }

    #[tokio::test]
    async fn a_failed_chunk_is_fetched_again() {
        let fake = Fake::new(10 << 20, true);
        let got = collect(body(fake.clone(), 0, (10 << 20) - 1).await.unwrap()).await;
        assert_eq!(got, fake.data);
    }

    #[tokio::test]
    async fn a_dropped_response_stops_fetching() {
        let fake = Fake::new(200 << 20, false);
        let s = body(fake.clone(), 0, (200 << 20) - 1).await.unwrap();
        futures::pin_mut!(s);
        s.next().await.unwrap().unwrap();
        s.next().await.unwrap().unwrap();
        drop(s);
        tokio::time::sleep(Duration::from_millis(200)).await;
        let started = fake.started.load(Ordering::SeqCst);
        tokio::time::sleep(Duration::from_millis(200)).await;
        assert_eq!(fake.started.load(Ordering::SeqCst), started, "nothing new after the drop");
        assert!(started <= 2 + PARALLEL + 1, "{started} chunks fetched for 2 read");
        assert_eq!(fake.running.load(Ordering::SeqCst), 0, "in-flight fetches aborted");
    }

    struct Quota;
    impl RangeSource for Quota {
        async fn fetch(&self, _: u64, _: u64) -> Result<Bytes, FetchError> {
            Err(FetchError::Fatal(anyhow::anyhow!("quota")))
        }
    }

    #[tokio::test]
    async fn a_final_error_on_the_first_chunk_fails_the_request() {
        let started = std::time::Instant::now();
        let err = body(Arc::new(Quota), 0, 100).await.err().unwrap();
        assert!(err.to_string().contains("quota"));
        assert!(started.elapsed() < Duration::from_millis(200), "not retried");
    }
}
