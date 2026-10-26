//! HTTP `Range: bytes=...` parsing (single range only, which is all players use).

#[derive(Debug, PartialEq, Eq)]
pub enum RangeError {
    Unsatisfiable,
}

/// Returns the inclusive `(start, end)` byte range to serve, or `None` for a full response.
pub fn parse(header: Option<&str>, len: u64) -> Result<Option<(u64, u64)>, RangeError> {
    let Some(spec) = header.and_then(|h| h.trim().strip_prefix("bytes=")) else {
        return Ok(None);
    };
    // Multi-range requests are answered with the first range.
    let spec = spec.split(',').next().unwrap_or("").trim();
    let (start, end) = spec.split_once('-').ok_or(RangeError::Unsatisfiable)?;
    let (start, end) = (start.trim(), end.trim());

    if len == 0 {
        return Err(RangeError::Unsatisfiable);
    }
    let last = len - 1;
    let range = match (start.is_empty(), end.is_empty()) {
        // bytes=-N : last N bytes
        (true, false) => {
            let n: u64 = end.parse().map_err(|_| RangeError::Unsatisfiable)?;
            if n == 0 {
                return Err(RangeError::Unsatisfiable);
            }
            (len.saturating_sub(n), last)
        }
        (false, _) => {
            let s: u64 = start.parse().map_err(|_| RangeError::Unsatisfiable)?;
            let e: u64 = if end.is_empty() {
                last
            } else {
                end.parse::<u64>().map_err(|_| RangeError::Unsatisfiable)?.min(last)
            };
            if s > last || s > e {
                return Err(RangeError::Unsatisfiable);
            }
            (s, e)
        }
        (true, true) => return Err(RangeError::Unsatisfiable),
    };
    Ok(Some(range))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn no_header_means_full() {
        assert_eq!(parse(None, 100), Ok(None));
        assert_eq!(parse(Some("items=0-1"), 100), Ok(None));
    }

    #[test]
    fn open_ended() {
        assert_eq!(parse(Some("bytes=10-"), 100), Ok(Some((10, 99))));
        assert_eq!(parse(Some("bytes=0-"), 100), Ok(Some((0, 99))));
    }

    #[test]
    fn closed_and_clamped() {
        assert_eq!(parse(Some("bytes=10-19"), 100), Ok(Some((10, 19))));
        assert_eq!(parse(Some("bytes=90-500"), 100), Ok(Some((90, 99))));
    }

    #[test]
    fn suffix() {
        assert_eq!(parse(Some("bytes=-10"), 100), Ok(Some((90, 99))));
        assert_eq!(parse(Some("bytes=-500"), 100), Ok(Some((0, 99))));
    }

    #[test]
    fn first_of_multiple() {
        assert_eq!(parse(Some("bytes=0-1, 5-9"), 100), Ok(Some((0, 1))));
    }

    #[test]
    fn unsatisfiable() {
        for h in ["bytes=100-", "bytes=50-10", "bytes=-0", "bytes=-", "bytes=abc", "bytes=1-x"] {
            assert_eq!(parse(Some(h), 100), Err(RangeError::Unsatisfiable), "{h}");
        }
        assert_eq!(parse(Some("bytes=0-"), 0), Err(RangeError::Unsatisfiable));
    }
}
