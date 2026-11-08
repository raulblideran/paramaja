//! Localhost HTTP server mpv reads from, with Range support:
//!
//! * `GET|HEAD /stream/{torrent_id}/{file_id}` — a torrent file. Each request opens a
//!   librqbit `FileStream`, which fetches pieces in order ahead of its read position,
//!   so seeking in the player re-prioritises the download.
//! * `GET|HEAD /mega/{id}` — a MEGA file, fetched from MEGA's CDN and decrypted on the fly.
//! * `GET|HEAD /proxy/{id}/{name}` — a direct link (MEGA's desktop app), read ahead.
//!
//! MEGA and direct links are fetched several byte-range chunks at once (`parallel`): one
//! connection to MEGA is slower than a 4K remux.

use std::{
    net::{Ipv4Addr, SocketAddr},
    sync::Arc,
};

use axum::{
    Router,
    body::Body,
    extract::{Path, State},
    http::{HeaderMap, HeaderValue, Method, StatusCode, header, response::Builder},
    response::{IntoResponse, Response},
    routing::get,
};
use librqbit::{Session, api::TorrentIdOrHash};
use tokio::io::{AsyncReadExt, AsyncSeekExt};
use tokio_util::io::ReaderStream;

use crate::{mega, parallel, proxy, range, selection};

#[derive(Clone)]
pub struct AppState {
    pub session: Arc<Session>,
    pub mega: Arc<mega::MegaRegistry>,
    pub proxy: Arc<proxy::ProxyRegistry>,
}

pub async fn serve(state: AppState, port: u16) -> anyhow::Result<SocketAddr> {
    let app = Router::new()
        .route("/stream/{id}/{file}", get(stream).head(stream))
        .route("/mega/{id}", get(mega_stream).head(mega_stream))
        .route("/proxy/{id}/{name}", get(proxy_stream).head(proxy_stream))
        .route("/health", get(|| async { "ok" }))
        .with_state(state);
    let listener = tokio::net::TcpListener::bind((Ipv4Addr::LOCALHOST, port)).await?;
    let addr = listener.local_addr()?;
    tokio::spawn(async move {
        if let Err(e) = axum::serve(listener, app).await {
            tracing::error!("stream server stopped: {e:#}");
        }
    });
    Ok(addr)
}

type HttpError = (StatusCode, String);

fn into_response(r: Result<Response, HttpError>) -> Response {
    match r {
        Ok(r) => r,
        Err((status, msg)) => (status, msg).into_response(),
    }
}

/// The byte range to send and the response headers that go with it.
struct Planned {
    builder: Builder,
    start: u64,
    body_len: u64,
}

fn plan(len: u64, name: &str, headers: &HeaderMap) -> Result<Planned, Response> {
    let range_hdr = headers.get(header::RANGE).and_then(|v| v.to_str().ok());
    let range = match range::parse(range_hdr, len) {
        Ok(r) => r,
        Err(range::RangeError::Unsatisfiable) => {
            return Err(Response::builder()
                .status(StatusCode::RANGE_NOT_SATISFIABLE)
                .header(header::CONTENT_RANGE, format!("bytes */{len}"))
                .body(Body::empty())
                .unwrap());
        }
    };
    let (start, end) = range.unwrap_or((0, len.saturating_sub(1)));
    let body_len = if len == 0 { 0 } else { end - start + 1 };

    let mut builder = Response::builder()
        .status(if range.is_some() { StatusCode::PARTIAL_CONTENT } else { StatusCode::OK })
        .header(header::ACCEPT_RANGES, "bytes")
        .header(header::CONTENT_TYPE, HeaderValue::from_static(selection::mime_for(name)))
        .header(header::CONTENT_LENGTH, body_len);
    if range.is_some() {
        builder = builder.header(header::CONTENT_RANGE, format!("bytes {start}-{end}/{len}"));
    }
    Ok(Planned { builder, start, body_len })
}

async fn stream(
    State(state): State<AppState>,
    Path((id, file)): Path<(usize, usize)>,
    method: Method,
    headers: HeaderMap,
) -> Response {
    into_response(stream_inner(&state.session, id, file, &method, &headers).await)
}

async fn stream_inner(
    session: &Arc<Session>,
    id: usize,
    file: usize,
    method: &Method,
    headers: &HeaderMap,
) -> Result<Response, HttpError> {
    let not_found = |m: String| (StatusCode::NOT_FOUND, m);
    let handle = session
        .get(TorrentIdOrHash::Id(id))
        .ok_or_else(|| not_found(format!("no torrent {id}")))?;
    let (len, name) = handle
        .with_metadata(|m| {
            m.file_infos
                .get(file)
                .map(|fi| (fi.len, fi.relative_filename.to_string_lossy().into_owned()))
        })
        .map_err(|e| (StatusCode::SERVICE_UNAVAILABLE, format!("{e:#}")))?
        .ok_or_else(|| not_found(format!("no file {file}")))?;

    let p = match plan(len, &name, headers) {
        Ok(p) => p,
        Err(resp) => return Ok(resp),
    };
    if method == Method::HEAD || p.body_len == 0 {
        return Ok(p.builder.body(Body::empty()).unwrap());
    }

    let mut fs = handle
        .stream(file)
        .await
        .map_err(|e| (StatusCode::SERVICE_UNAVAILABLE, format!("{e:#}")))?;
    fs.seek(std::io::SeekFrom::Start(p.start))
        .await
        .map_err(|e| (StatusCode::INTERNAL_SERVER_ERROR, e.to_string()))?;
    // 1 MiB reads keep the per-chunk overhead negligible at 4K remux bitrates.
    let body = Body::from_stream(ReaderStream::with_capacity(fs.take(p.body_len), 1024 * 1024));
    Ok(p.builder.body(body).unwrap())
}

async fn mega_stream(
    State(state): State<AppState>,
    Path(id): Path<usize>,
    method: Method,
    headers: HeaderMap,
) -> Response {
    into_response(mega_inner(&state.mega, id, &method, &headers).await)
}

async fn mega_inner(
    registry: &Arc<mega::MegaRegistry>,
    id: usize,
    method: &Method,
    headers: &HeaderMap,
) -> Result<Response, HttpError> {
    let entry = registry
        .get(id)
        .ok_or_else(|| (StatusCode::NOT_FOUND, format!("no MEGA file {id}")))?;
    let file = entry.file.lock().unwrap().clone();
    let p = match plan(file.size, &file.name, headers) {
        Ok(p) => p,
        Err(resp) => return Ok(resp),
    };
    if method == Method::HEAD || p.body_len == 0 {
        return Ok(p.builder.body(Body::empty()).unwrap());
    }
    let source = Arc::new(mega::MegaSource { registry: registry.clone(), entry });
    let body = parallel::body(source, p.start, p.start + p.body_len - 1).await.map_err(|e| {
        if e.downcast_ref::<mega::QuotaExceeded>().is_some() {
            (StatusCode::from_u16(509).unwrap(), e.to_string())
        } else {
            (StatusCode::BAD_GATEWAY, format!("MEGA download failed: {e:#}"))
        }
    })?;
    Ok(p.builder.body(Body::from_stream(body)).unwrap())
}

async fn proxy_stream(
    State(state): State<AppState>,
    Path((id, _name)): Path<(usize, String)>,
    method: Method,
    headers: HeaderMap,
) -> Response {
    into_response(proxy_inner(&state.proxy, id, &method, &headers).await)
}

async fn proxy_inner(
    registry: &proxy::ProxyRegistry,
    id: usize,
    method: &Method,
    headers: &HeaderMap,
) -> Result<Response, HttpError> {
    let entry = registry
        .get(id)
        .ok_or_else(|| (StatusCode::NOT_FOUND, format!("no link {id}")))?;
    let p = match plan(entry.size, &entry.name, headers) {
        Ok(p) => p,
        Err(resp) => return Ok(resp),
    };
    if method == Method::HEAD || p.body_len == 0 {
        return Ok(p.builder.body(Body::empty()).unwrap());
    }
    let source = Arc::new(registry.source(entry));
    let body = parallel::body(source, p.start, p.start + p.body_len - 1)
        .await
        .map_err(|e| (StatusCode::BAD_GATEWAY, format!("the link failed: {e:#}")))?;
    Ok(p.builder.body(Body::from_stream(body)).unwrap())
}
