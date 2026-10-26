# Changelog

Versions follow `MAJOR.MINOR.PATCH`. The in-app version (welcome screen, Settings → About)
also shows the git commit a build came from, e.g. `1.1.0 (50919f7)`.

## 1.1.0 — 2026-10-26

- Controls come back when you move the mouse, including in fullscreen
- Deinterlacing: BWDIF by default, Bob as an option
- Subtitle size slider shows the font size
- Logarithmic volume slider (equal steps of loudness)
- Faster, smoother magnet streaming for 4K HDR: reads 256 MB ahead and buffers 5 s before starting
- Original audio by default; stereo downmix and AV-receiver passthrough are optional
- Play web links: YouTube and ~1800 other sites (via yt-dlp), MEGA file links, direct video URLs
- Buffering messages, including "Big fast, many speed" and "Bruney"
- Version shown in the app and in the download's file name

## 1.0.0 — 2026-10-26

- First release: HEVC/HDR playback with HDR→SDR tonemapping, magnet streaming, subtitles,
  uncompressed PNG screenshots, frame stepping, automatic deinterlacing, portable Windows build
