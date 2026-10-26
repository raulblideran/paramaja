//! Picking which file in a torrent to play and what MIME type to serve it as.

use serde::Serialize;

const VIDEO_EXTS: &[&str] = &[
    "mkv", "mp4", "m4v", "avi", "mov", "wmv", "ts", "m2ts", "mts", "webm", "flv", "mpg", "mpeg",
    "vob", "ogv", "3gp", "hevc", "h265", "h264", "rmvb", "divx", "y4m",
];

#[derive(Debug, Clone, Serialize, PartialEq)]
pub struct FileEntry {
    pub index: usize,
    pub path: String,
    pub len: u64,
    pub is_video: bool,
}

fn extension(path: &str) -> String {
    path.rsplit_once('.')
        .map(|(_, e)| e.to_ascii_lowercase())
        .unwrap_or_default()
}

pub fn is_video(path: &str) -> bool {
    VIDEO_EXTS.contains(&extension(path).as_str())
}

/// "Sample" clips shipped with releases shouldn't win over the main feature.
pub fn is_sample(path: &str) -> bool {
    path.to_ascii_lowercase()
        .split(|c: char| !c.is_ascii_alphanumeric())
        .any(|tok| tok == "sample" || tok == "trailer")
}

/// Largest non-sample video, else largest video, else largest file.
pub fn pick_default(files: &[FileEntry]) -> Option<usize> {
    let largest = |pred: &dyn Fn(&FileEntry) -> bool| {
        files.iter().filter(|f| pred(f)).max_by_key(|f| f.len).map(|f| f.index)
    };
    largest(&|f| f.is_video && !is_sample(&f.path))
        .or_else(|| largest(&|f| f.is_video))
        .or_else(|| largest(&|_| true))
}

pub fn mime_for(path: &str) -> &'static str {
    match extension(path).as_str() {
        "mkv" => "video/x-matroska",
        "mp4" | "m4v" => "video/mp4",
        "webm" => "video/webm",
        "avi" => "video/x-msvideo",
        "mov" => "video/quicktime",
        "ts" | "m2ts" | "mts" => "video/mp2t",
        "mpg" | "mpeg" | "vob" => "video/mpeg",
        "wmv" => "video/x-ms-wmv",
        "flv" => "video/x-flv",
        "ogv" => "video/ogg",
        "srt" => "application/x-subrip",
        "ass" | "ssa" => "text/x-ssa",
        _ => "application/octet-stream",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn f(index: usize, path: &str, len: u64) -> FileEntry {
        FileEntry { index, path: path.into(), len, is_video: is_video(path) }
    }

    #[test]
    fn detects_video_extensions_case_insensitively() {
        assert!(is_video("Movie.2160p.MKV"));
        assert!(is_video("a/b/c.m2ts"));
        assert!(!is_video("subs.srt"));
        assert!(!is_video("noext"));
    }

    #[test]
    fn picks_largest_video_over_bigger_non_video() {
        let files = [f(0, "extras.iso", 9_000), f(1, "movie.mkv", 5_000), f(2, "small.mp4", 10)];
        assert_eq!(pick_default(&files), Some(1));
    }

    #[test]
    fn skips_sample_files() {
        let files = [f(0, "Sample/movie-sample.mkv", 9_000), f(1, "movie.mkv", 5_000)];
        assert_eq!(pick_default(&files), Some(1));
    }

    #[test]
    fn falls_back_to_sample_then_any_file() {
        assert_eq!(pick_default(&[f(0, "sample.mkv", 1), f(1, "x.txt", 50)]), Some(0));
        assert_eq!(pick_default(&[f(0, "a.txt", 1), f(1, "b.nfo", 50)]), Some(1));
        assert_eq!(pick_default(&[]), None);
    }

    #[test]
    fn sample_detection_is_token_based() {
        assert!(is_sample("Movie.Sample.mkv"));
        assert!(!is_sample("Samples of Life.mkv"));
    }

    #[test]
    fn mime_types() {
        assert_eq!(mime_for("x.MKV"), "video/x-matroska");
        assert_eq!(mime_for("x.mp4"), "video/mp4");
        assert_eq!(mime_for("x.bin"), "application/octet-stream");
    }
}
