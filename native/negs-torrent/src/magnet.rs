//! Magnet link / info-hash parsing.
//!
//! librqbit parses the standard fields; we additionally honour `x.pe` (BEP 9 peer
//! addresses) so a magnet can point straight at a known peer.

use std::net::{SocketAddr, ToSocketAddrs};

use anyhow::{Context, bail};

#[derive(Debug, Clone, PartialEq)]
pub struct ParsedMagnet {
    /// Normalised magnet URI handed to librqbit.
    pub uri: String,
    /// Lowercase hex v1 info hash.
    pub info_hash: String,
    pub name: Option<String>,
    pub trackers: Vec<String>,
    pub peers: Vec<SocketAddr>,
}

pub fn parse(input: &str) -> anyhow::Result<ParsedMagnet> {
    let input = input.trim();
    if input.is_empty() {
        bail!("empty magnet link");
    }

    // A bare 40-char hex info hash is accepted as a convenience.
    if input.len() == 40 && input.chars().all(|c| c.is_ascii_hexdigit()) {
        let hash = input.to_ascii_lowercase();
        return Ok(ParsedMagnet {
            uri: format!("magnet:?xt=urn:btih:{hash}"),
            info_hash: hash,
            name: None,
            trackers: vec![],
            peers: vec![],
        });
    }

    if !input.to_ascii_lowercase().starts_with("magnet:?") {
        bail!("not a magnet link (expected it to start with \"magnet:?\")");
    }

    let magnet = librqbit::Magnet::parse(input).context("invalid magnet link")?;
    let id20 = magnet
        .as_id20()
        .context("magnet link has no BitTorrent v1 (btih) info hash")?;

    let url = url::Url::parse(input).context("invalid magnet link")?;
    let peers = url
        .query_pairs()
        .filter(|(k, _)| k == "x.pe")
        .filter_map(|(_, v)| v.to_socket_addrs().ok().and_then(|mut a| a.next()))
        .collect();

    Ok(ParsedMagnet {
        uri: input.to_owned(),
        info_hash: id20.as_string(),
        name: magnet.name.clone(),
        trackers: magnet.trackers.clone(),
        peers,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const HASH: &str = "08ada5a7a6183aae1e09d831df6748d566095a10";

    #[test]
    fn parses_full_magnet() {
        let m = parse(&format!(
            "magnet:?xt=urn:btih:{}&dn=Sintel&tr=udp%3A%2F%2Ftracker.example%3A1337",
            HASH.to_uppercase()
        ))
        .unwrap();
        assert_eq!(m.info_hash, HASH);
        assert_eq!(m.name.as_deref(), Some("Sintel"));
        assert_eq!(m.trackers, vec!["udp://tracker.example:1337"]);
        assert!(m.peers.is_empty());
    }

    #[test]
    fn accepts_bare_info_hash() {
        let m = parse(HASH).unwrap();
        assert_eq!(m.uri, format!("magnet:?xt=urn:btih:{HASH}"));
    }

    #[test]
    fn trims_whitespace() {
        assert!(parse(&format!("  magnet:?xt=urn:btih:{HASH}\n")).is_ok());
    }

    #[test]
    fn reads_peer_addresses() {
        let m = parse(&format!(
            "magnet:?xt=urn:btih:{HASH}&x.pe=127.0.0.1:6881&x.pe=bogus"
        ))
        .unwrap();
        assert_eq!(m.peers, vec!["127.0.0.1:6881".parse().unwrap()]);
    }

    #[test]
    fn rejects_garbage() {
        for bad in ["", "   ", "http://example.com", "magnet:?dn=nohash", "magnet:?xt=urn:btih:zz", "1234"] {
            assert!(parse(bad).is_err(), "{bad:?} should be rejected");
        }
    }
}
