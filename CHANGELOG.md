# Changelog

Versions follow `MAJOR.MINOR.PATCH`. The in-app version (welcome screen, Settings → About)
also shows the git commit a build came from, e.g. `1.1.0 (50919f7)`.

## 1.3.0 — 2026-10-26

- One **Open** menu: Open file, Open folder (Ctrl+Shift+O) and Open link. Folders and
  drag & drop of folders: a DVD folder (VIDEO_TS) or Blu-ray folder (BDMV) plays as a
  disc from its main title; any other folder plays its videos as a playlist. Unencrypted
  discs only
- Uncompressed AVI (8-bit and 10-bit capture formats) and DVD VOB files are tested to play
- Download button at the top while a YouTube (or other site's) video plays: saves the best
  quality, video and audio merged into one MKV, to your Downloads folder, with progress and
  Cancel. The Windows build now includes ffmpeg.exe for this; the Linux Flatpak now
  includes yt-dlp, so YouTube links also play there
- HDR is automatic: on a monitor with HDR switched on (Windows), HDR video goes to it
  untouched; on an SDR monitor it is tonemapped. Always tonemap / always passthrough can
  still be chosen, and H still switches by hand. Tonemapping options moved under
  "Advanced", hidden on HDR monitors. Linux always tonemaps (X11 can't output HDR)
- Rendering quality (Auto / High / Balanced / Fast) moved under "Advanced"
- Bob deinterlacing removed: it squashed the picture, and BWDIF already gives one frame
  per field (50i becomes 50p)
- Double-click on the video enters and leaves fullscreen
- The mouse pointer hides together with the controls, including in fullscreen

## 1.2.0 — 2026-10-26

- Smooth 4K on weaker GPUs. 4K costs about five times as much to render as 1080p, which is
  why 4K could stutter while 1080p was fine. Settings → Video → Rendering:
  Auto (default) uses High below 4K and Balanced for 4K, and switches to Fast by itself if
  frames still drop; High, Balanced and Fast can be picked directly
- Slow connections: each time a stream has to stop and buffer, it now buffers longer
  before resuming (5 s, then 10, 20, 40, up to 60), so there are fewer interruptions
- Copy diagnostics (Ctrl+I, or Settings → About): copies the machine, the video, and why
  playback is struggling, to paste to whoever is helping

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
