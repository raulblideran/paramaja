# Changelog

Versions follow `MAJOR.MINOR.PATCH`. The in-app version (welcome screen, Settings → About)
also shows the git commit a build came from, e.g. `1.1.0 (50919f7)`.

## 1.5.0 — 2026-10-27

- **Downloader:** a Download button (welcome screen, the Open menus, and the top bar while a
  web video plays). Paste a link, pick a quality from the ones the site offers (2160p, 1080p,
  720p…), and it's saved to Downloads with the best audio merged in
- **Streaming quality** for YouTube and other sites: Settings → Playback, from best available
  down to 360p
- Separate **Open link** button with its own icon, on the welcome screen and in the controls
- Subtitle timing (Z / X) shows the total shift on screen with every press: "+0.1s", "+0.2s"…
- Hardware acceleration is on for HEVC at any size and for every codec from 1080p up
- Playback is timed for a 60 Hz display by default, which fixes stutter on monitors that report
  odd refresh rates. Settings → Video → Advanced has Auto and other rates
- Rendering is **Balanced** by default; whoever was on Auto moves to Balanced once. Only Auto
  switches to Fast by itself when frames keep dropping
- **Volume normalization** switch (off by default) in the audio menu and Settings
- **Mono downmix** for stereo sources only; the stereo downmix still applies to 5.1 / 7.1 only
- Leaving fullscreen goes back to a maximized window

## 1.4.0 — 2026-10-26

- **Titles & chapters** menu: jump to any chapter, or switch a DVD's or Blu-ray's title
  (main film, extras) at any moment. Previous / next chapter buttons and Page Up / Page Down.
  Works for any video with chapters, MKVs included. The discs' own interactive menus aren't
  shown yet
- The video menu's HDR options (tonemapping) appear only while an HDR video plays
- Hardware acceleration is used for HEVC (H.265) only; other codecs decode in software. Its
  switch appears in the video menu only while an HEVC video plays
- The "Downmix to stereo" switch appears only for 5.1 / 7.1 audio, not for mono or stereo
- Windows: double-click on the video for fullscreen now works; clicks are read from
  Windows itself, as the video window didn't reliably pass them on
- Releases go up as soon as the Windows zip is built; the Linux Flatpak is attached a few
  minutes later

## 1.3.3 — 2026-10-26

- Windows: closing the window right as it was being resized could raise an error
- Linux: works with an older system libmpv too (Ubuntu 24.04's 0.37): streams open and the
  player starts, where before they failed. The Flatpak bundles its own libmpv and wasn't
  affected
- Releases are now built and published by GitHub from the tagged commit, and every change is
  tested on Windows and Linux

## 1.3.2 — 2026-10-26

- Fullscreen on Linux (KDE): the controls bar now really sits on the bottom edge. KDE kept
  ordinary windows out of the space its taskbar reserves, even with the video fullscreen
  over the taskbar, which left a strip of video below the bar (1.3.1 didn't fix this).
  The bars are now dock windows, which may go there, and they hide while another app is
  in front

## 1.3.1 — 2026-10-26

- In fullscreen the controls bar sits on the bottom edge of the screen again, with no strip of video below it

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
