//! Localhost HTTP server: `GET|HEAD /stream/{torrent_id}/{file_id}` with Range support.
//!
//! Each request opens a librqbit `FileStream`, which prioritises the pieces right
//! after its read position — so seeking in the player re-prioritises the download.

use std::{net::{Ipv4Addr, SocketAddr}, sync::Arc};

use axum::{
    Router,
    body::Body,
    extract::{Path, State},
    http::{HeaderMap, HeaderValue, Method, StatusCode, header},
    response::{IntoResponse, Response},
    routing::get,
};
use librqbit::{Session, api::TorrentIdOrHash};
use tokio::io::{AsyncReadExt, AsyncSeekExt};
use tokio_util::io::ReaderStream;

use crate::{range, selection};

pub async fn serve(session: Arc<Session>, port: u16) -> anyhow::Result<SocketAddr> {
    let app = Router::new()
        .route("/stream/{id}/{file}", get(stream).head(stream))
        .route("/health", get(|| async { "ok" }))
        .with_state(session);
    let listener = tokio::net::TcpListener::bind((Ipv4Addr::LOCALHOST, port)).await?;
    let addr = listener.local_addr()?;
    tokio::spawn(async move {
        if let Err(e) = axum::serve(listener, app).await {
            tracing::error!("stream server stopped: {e:#}");
        }
    });
    Ok(addr)
}

async fn stream(
    State(session): State<Arc<Session>>,
    Path((id, file)): Path<(usize, usize)>,
    method: Method,
    headers: HeaderMap,
) -> Response {
    match stream_inner(&session, id, file, &method, &headers).await {
        Ok(r) => r,
        Err((status, msg)) => (status, msg).into_response(),
    }
}

async fn stream_inner(
    session: &Arc<Session>,
    id: usize,
    file: usize,
    method: &Method,
    headers: &HeaderMap,
) -> Result<Response, (StatusCode, String)> {
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

    let range_hdr = headers.get(header::RANGE).and_then(|v| v.to_str().ok());
    let range = match range::parse(range_hdr, len) {
        Ok(r) => r,
        Err(range::RangeError::Unsatisfiable) => {
            return Ok(Response::builder()
                .status(StatusCode::RANGE_NOT_SATISFIABLE)
                .header(header::CONTENT_RANGE, format!("bytes */{len}"))
                .body(Body::empty())
                .unwrap());
        }
    };
    let (start, end) = range.unwrap_or((0, len.saturating_sub(1)));
    let body_len = if len == 0 { 0 } else { end - start + 1 };

    let mut resp = Response::builder()
        .status(if range.is_some() { StatusCode::PARTIAL_CONTENT } else { StatusCode::OK })
        .header(header::ACCEPT_RANGES, "bytes")
        .header(header::CONTENT_TYPE, HeaderValue::from_static(selection::mime_for(&name)))
        .header(header::CONTENT_LENGTH, body_len);
    if range.is_some() {
        resp = resp.header(header::CONTENT_RANGE, format!("bytes {start}-{end}/{len}"));
    }

    if method == Method::HEAD || body_len == 0 {
        return Ok(resp.body(Body::empty()).unwrap());
    }

    let mut fs = handle
        .stream(file)
        .await
        .map_err(|e| (StatusCode::SERVICE_UNAVAILABLE, format!("{e:#}")))?;
    fs.seek(std::io::SeekFrom::Start(start))
        .await
        .map_err(|e| (StatusCode::INTERNAL_SERVER_ERROR, e.to_string()))?;
    let body = Body::from_stream(ReaderStream::with_capacity(fs.take(body_len), 64 * 1024));
    Ok(resp.body(body).unwrap())
}
