//! MEGA public file links.
//!
//! A link carries the file key after the `#`. The file is fetched from MEGA's CDN and
//! decrypted on the fly with AES-128-CTR; CTR is seekable, so the localhost stream
//! server can answer Range requests and the player can seek freely.

use std::{
    collections::HashMap,
    sync::{
        Arc, Mutex,
        atomic::{AtomicUsize, Ordering},
    },
};

use aes::{
    Aes128,
    cipher::{BlockDecryptMut, KeyIvInit, StreamCipher, StreamCipherSeek},
};
use anyhow::{Context, bail};
use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
use serde::Serialize;
use serde_json::{Value, json};

pub const DEFAULT_API: &str = "https://g.api.mega.co.nz";

pub type Ctr = ctr::Ctr128BE<Aes128>;

#[derive(Debug, Clone, PartialEq)]
pub struct MegaLink {
    pub id: String,
    pub key: [u8; 16],
    pub iv: [u8; 16],
}

/// Accepts `https://mega.nz/file/<id>#<key>` and the legacy `https://mega.nz/#!<id>!<key>`
/// (also on mega.co.nz).
pub fn parse_link(input: &str) -> anyhow::Result<MegaLink> {
    let url = url::Url::parse(input.trim()).context("not a valid MEGA link")?;
    let host = url.host_str().unwrap_or("");
    if !matches!(host, "mega.nz" | "www.mega.nz" | "mega.co.nz" | "www.mega.co.nz" | "mega.io") {
        bail!("not a MEGA link");
    }
    let fragment = url.fragment().unwrap_or("");
    let (id, key) = if let Some(rest) = url.path().strip_prefix("/file/") {
        (rest.trim_end_matches('/').to_owned(), fragment.split('/').next().unwrap_or("").to_owned())
    } else if let Some(rest) = fragment.strip_prefix('!') {
        let mut parts = rest.splitn(2, '!');
        (parts.next().unwrap_or("").to_owned(), parts.next().unwrap_or("").to_owned())
    } else if url.path().starts_with("/folder/") || fragment.starts_with("F!") {
        bail!("MEGA folder links aren't supported yet; open the file's own link instead");
    } else {
        bail!("unrecognised MEGA link");
    };
    if id.is_empty() {
        bail!("MEGA link has no file id");
    }
    if key.is_empty() {
        bail!("MEGA link has no decryption key (the part after #)");
    }
    let raw = URL_SAFE_NO_PAD
        .decode(key.trim_end_matches('='))
        .context("MEGA link key is not valid base64")?;
    if raw.len() != 32 {
        bail!("MEGA link key has the wrong length ({} bytes, expected 32)", raw.len());
    }
    let mut k = [0u8; 16];
    for i in 0..16 {
        k[i] = raw[i] ^ raw[i + 16];
    }
    let mut iv = [0u8; 16];
    iv[..8].copy_from_slice(&raw[16..24]);
    Ok(MegaLink { id, key: k, iv })
}

/// A keystream positioned at `offset` bytes into the file.
pub fn decryptor_at(link: &MegaLink, offset: u64) -> Ctr {
    let mut c = Ctr::new(&link.key.into(), &link.iv.into());
    c.seek(offset);
    c
}

pub fn decrypt_in_place(cipher: &mut Ctr, data: &mut [u8]) {
    cipher.apply_keystream(data);
}

/// File attributes are `MEGA{"n":"name",...}`, AES-128-CBC with a zero IV, base64url.
pub fn decrypt_attributes(at: &str, key: &[u8; 16]) -> anyhow::Result<Value> {
    let mut data = URL_SAFE_NO_PAD.decode(at.trim_end_matches('=')).context("bad attribute encoding")?;
    if data.is_empty() || data.len() % 16 != 0 {
        bail!("bad attribute length");
    }
    let mut dec = cbc::Decryptor::<Aes128>::new(key.into(), &[0u8; 16].into());
    for block in data.chunks_exact_mut(16) {
        dec.decrypt_block_mut(block.into());
    }
    let text = String::from_utf8_lossy(&data);
    let json = text
        .trim_end_matches('\0')
        .strip_prefix("MEGA")
        .context("wrong key for this MEGA link")?;
    Ok(serde_json::from_str(json).context("bad attribute JSON")?)
}

#[derive(Debug, Clone, Serialize)]
pub struct MegaInfo {
    pub id: usize,
    pub name: String,
    pub size: u64,
    pub stream_url: String,
}

#[derive(Debug, Clone)]
pub struct MegaFile {
    pub url: String,
    pub size: u64,
    pub name: String,
}

fn api_error(code: i64) -> String {
    match code {
        -2 => "MEGA rejected the request (bad link?)".into(),
        -9 => "MEGA says this file doesn't exist (removed, or the link is wrong)".into(),
        -11 | -16 => "MEGA has blocked access to this file".into(),
        -17 | -18 => "MEGA is temporarily unavailable, or the transfer quota is used up; try again later".into(),
        c => format!("MEGA API error {c}"),
    }
}

/// Asks the MEGA API for a temporary download URL plus the file's size and name.
pub async fn resolve(client: &reqwest::Client, api: &str, link: &MegaLink) -> anyhow::Result<MegaFile> {
    let seq: u32 = rand::random();
    let resp: Value = client
        .post(format!("{api}/cs?id={seq}"))
        .json(&json!([{ "a": "g", "g": 1, "ssl": 2, "p": link.id }]))
        .send()
        .await
        .context("couldn't reach MEGA")?
        .error_for_status()
        .context("MEGA API request failed")?
        .json()
        .await
        .context("unexpected MEGA API response")?;
    let item = match &resp {
        Value::Number(n) => bail!(api_error(n.as_i64().unwrap_or(0))),
        Value::Array(a) => a.first().cloned().context("empty MEGA API response")?,
        _ => bail!("unexpected MEGA API response"),
    };
    if let Some(code) = item.as_i64() {
        bail!(api_error(code));
    }
    let url = item["g"].as_str().context("MEGA didn't return a download URL")?.to_owned();
    let size = item["s"].as_u64().context("MEGA didn't return the file size")?;
    let name = item["at"]
        .as_str()
        .and_then(|at| decrypt_attributes(at, &link.key).ok())
        .and_then(|a| a["n"].as_str().map(str::to_owned))
        .unwrap_or_else(|| format!("{}.mkv", link.id));
    Ok(MegaFile { url, size, name })
}

pub struct MegaEntry {
    pub link: MegaLink,
    pub file: Mutex<MegaFile>,
}

/// Links resolved this session, addressed by small ids in stream URLs.
pub struct MegaRegistry {
    pub client: reqwest::Client,
    pub api: String,
    next: AtomicUsize,
    entries: Mutex<HashMap<usize, Arc<MegaEntry>>>,
}

impl MegaRegistry {
    pub fn new(api: String) -> Self {
        Self {
            client: reqwest::Client::builder()
                .user_agent("Paramaja")
                .build()
                .expect("http client"),
            api,
            next: AtomicUsize::new(0),
            entries: Mutex::new(HashMap::new()),
        }
    }

    pub async fn add(&self, input: &str) -> anyhow::Result<(usize, MegaFile)> {
        let link = parse_link(input)?;
        let file = resolve(&self.client, &self.api, &link).await?;
        let id = self.next.fetch_add(1, Ordering::Relaxed);
        self.entries
            .lock()
            .unwrap()
            .insert(id, Arc::new(MegaEntry { link, file: Mutex::new(file.clone()) }));
        Ok((id, file))
    }

    pub fn get(&self, id: usize) -> Option<Arc<MegaEntry>> {
        self.entries.lock().unwrap().get(&id).cloned()
    }

    /// Download URLs expire; fetch a fresh one.
    pub async fn refresh(&self, entry: &MegaEntry) -> anyhow::Result<MegaFile> {
        let file = resolve(&self.client, &self.api, &entry.link).await?;
        *entry.file.lock().unwrap() = file.clone();
        Ok(file)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use aes::cipher::BlockEncryptMut;

    fn link_key(raw: &[u8; 32]) -> String {
        URL_SAFE_NO_PAD.encode(raw)
    }

    #[test]
    fn parses_both_link_formats() {
        let raw: [u8; 32] = std::array::from_fn(|i| i as u8);
        let key = link_key(&raw);
        let a = parse_link(&format!("https://mega.nz/file/AbC123#{key}")).unwrap();
        let b = parse_link(&format!("https://mega.co.nz/#!AbC123!{key}")).unwrap();
        assert_eq!(a, b);
        assert_eq!(a.id, "AbC123");
        assert_eq!(a.key[0], raw[0] ^ raw[16]);
        assert_eq!(&a.iv[..8], &raw[16..24]);
        assert_eq!(&a.iv[8..], &[0u8; 8]);
    }

    #[test]
    fn rejects_bad_links() {
        let key = link_key(&[1u8; 32]);
        for (bad, why) in [
            ("https://example.com/file/x#y".to_owned(), "not a MEGA"),
            ("https://mega.nz/file/AbC123".to_owned(), "no decryption key"),
            ("https://mega.nz/file/AbC123#c2hvcnRrZXk".to_owned(), "wrong length"),
            ("https://mega.nz/file/AbC123#not*base64".to_owned(), "not valid base64"),
            (format!("https://mega.nz/folder/AbC123#{key}"), "folder"),
            ("not a url".to_owned(), "valid MEGA"),
        ] {
            let err = parse_link(&bad).unwrap_err().to_string();
            assert!(err.contains(why), "{bad}: {err}");
        }
    }

    #[test]
    fn ctr_decryption_can_start_anywhere() {
        let link = parse_link(&format!("https://mega.nz/file/x#{}", link_key(&[7u8; 32]))).unwrap();
        let plain: Vec<u8> = (0..10_000u32).map(|i| (i % 251) as u8).collect();
        let mut enc = plain.clone();
        decrypt_in_place(&mut decryptor_at(&link, 0), &mut enc); // CTR: encrypt == decrypt
        assert_ne!(enc, plain);
        for start in [0usize, 1, 15, 16, 17, 4095, 9999] {
            let mut part = enc[start..].to_vec();
            decrypt_in_place(&mut decryptor_at(&link, start as u64), &mut part);
            assert_eq!(part, plain[start..], "offset {start}");
        }
    }

    #[test]
    fn decrypts_attributes() {
        let key = [9u8; 16];
        let mut data = br#"MEGA{"n":"Big Buck Bunny.mkv","c":"x"}"#.to_vec();
        data.resize(data.len().div_ceil(16) * 16, 0);
        let mut enc = cbc::Encryptor::<Aes128>::new(&key.into(), &[0u8; 16].into());
        for block in data.chunks_exact_mut(16) {
            enc.encrypt_block_mut(block.into());
        }
        let attrs = decrypt_attributes(&URL_SAFE_NO_PAD.encode(&data), &key).unwrap();
        assert_eq!(attrs["n"], "Big Buck Bunny.mkv");
        assert!(decrypt_attributes(&URL_SAFE_NO_PAD.encode(&data), &[1u8; 16]).is_err());
    }

    #[test]
    fn api_errors_are_readable() {
        assert!(api_error(-9).contains("doesn't exist"));
        assert!(api_error(-18).contains("quota"));
    }
}
