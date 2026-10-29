# Changelog

Versions follow `MAJOR.MINOR.PATCH`. The in-app version (welcome screen, Settings → About)
also shows the git commit a build came from, e.g. `1.1.0 (50919f7)`.

## 1.9.0 — 2026-10-29

- **Disc images:** a DVD or Blu-ray `.iso` plays like the disc, menus included (Open Disc →
  **Disc image…**, or open / drop the file). So does an Audio CD image (`.bin` with its `.cue`),
  with its tracks as chapters, named as its cue sheet names them. Other `.iso` files are left
  alone.
- **SACD images:** the `.iso` a ripper makes of an SACD plays with Paramaja's own SACD reader,
  the stereo area by default; the **Titles & chapters** menu has the multichannel (5.1) area,
  and the tracks are chapters, with the disc's titles. Played through on two Thriller SACDs
  (1999 and the 2022 MFSL), whose stereo areas are plain DSD. DST-compressed discs (most
  multichannel areas) are read too, but were only tested on images made for the purpose.
- **DSD files** (`.dsf`, `.dff`) play, and are picked up from folders.
- **Stop button** in the controls (Ctrl+S, as before).
- **Torrent playlists:** a torrent with several videos or songs gets **Previous** / **Next**
  buttons, and each file plays on into the next. Music counts now, not just videos; an album
  starts at its first track.
- **Resize to 4:3 / Crop to 16:9** in the video menu, for the playing video: resize for any
  picture that isn't 4:3, crop (the middle of it) for any that isn't 16:9. A DVD's shape comes
  from the disc itself (libVLC doesn't tell it), so a 4:3 DVD offers only the crop.
- **YouTube quality in the video menu:** while a YouTube video plays, the menu lists the
  qualities it offers and switches to one at once, from where it was, for that video. The
  default quality moved to Settings → Developer. Both are for YouTube only: other sites
  (archive.org, Vimeo…) play at their best, and direct links (MEGA's desktop app streaming
  included) are left to the player alone.
- **DVD badge:** a DVD shows "DVD" in the controls instead of its codec (MPEG-2 on hover).
- **5.1 stays 5.1 on discs:** DVDs and Blu-rays played their 5.1 tracks downmixed to stereo,
  as libVLC's default stereo mode does that. They now keep every channel (a stereo track is
  still picked first where a disc has one).
- The deinterlacing, hardware decoding, HDR and passthrough switches moved to **Settings →
  Developer** (the first two still for the playing video only); the player's menus keep the
  everyday options. The Video tab is gone (its options moved with them).
- The previous / next frame buttons, and the video menu, are hidden while only audio plays
  (music, Audio CDs, SACDs).
- Messages on the video (volume, subtitle timing, "Screenshot saved"…) start below the title
  bar instead of under it, and the buffering lines show in one place at a time.
- Linux: **Space** (and the other keys) stopped working after a click on the controls, until the
  window was minimized and restored: the hidden controls kept the keyboard. They give it back.
- Linux: **double-click** for fullscreen works, on discs too: clicks are read from the X server
  itself, as on Windows from Windows.
- Opening the same interlaced video twice in a row could leave the second without
  deinterlacing. Likewise, after two files alike (two stereo songs), the audio badge and the
  left / right channel choice could go missing, and the length shown could be the previous one's.

## 1.8.0 — 2026-10-28

- **Deinterlacing only where the video is interlaced.** The deinterlace badge showed for
  progressive videos too: discs reported it whatever they held, and it stayed up when a file
  played after a disc. Now each video is judged on its own:
  - Files: by their frames' interlace flags. Videos the CPU decodes are also checked by their
    picture (FFmpeg's idet), so tape captures saved without flags (MJPEG or lossless AVIs,
    some FLVs) get BWDIF too. Progressive video is left alone.
  - Discs: by libVLC's own detection.

  While an interlaced video plays, the video menu has an **on / off** switch for it (D flips it).
  Progressive videos have no switch. The Auto / On / Off setting is gone.
- **Smoother playback at 1080p and up on laptops.** This targets lag seen in High and Balanced
  on a Windows laptop with two GPUs and a high-refresh monitor:
  - Every profile now times frames by the audio clock, as Fast already did. The fixed 60 Hz
    display timing could clash with a 120–165 Hz monitor. Syncing to the display is now an
    option in Settings → Developer.
  - On laptops with two GPUs, playback and hardware decoding run on the high-performance one,
    instead of the power-saving one Windows hands out (Settings → Developer).
  - The controls bar no longer redraws with every video frame.

  If it still lags, play the video and use **Settings → Developer → Create log**, then send the
  file from the logs folder.
- **Stereo by default:** where a video or disc has several audio tracks (a concert disc's
  stereo mix beside 5.1 and DTS), the stereo one plays, within your preferred language if you
  set one
- **Left or right channel:** for a stereo source, the audio menu can play just its left or right
  channel on both speakers, for that video. The mono downmix is gone
- **Codec badge** in the controls: H.264, HEVC, AV1, MPEG-2… (the decoder's full name on hover)
- **Developer tab** in Settings: rendering quality, frame timing, tonemapping and the GPU choice
  moved there from the video menu and Settings → Video, with the troubleshooting tools
  (statistics overlay, Copy diagnostics, Create log)
- **Create log:** saves to the logs folder everything recorded since the playing video started:
  - every second's statistics: frames dropped and late, GPU time per frame, CPU use,
    refresh rates
  - the machine and its GPUs and displays
  - how the video is played, and the player's latest messages

  For benchmarking, or for whoever is helping
- The play / pause button no longer shows "play" while a video plays (after a disc, or a missed
  event)
- Menus no longer jump to the top-left corner of the screen when the pointer rests on them:
  the controls stay up while one of their menus is open
- The **Recent** list is gone from the welcome screen, and the app no longer keeps a history of
  what was played (the old list is dropped from settings.json)

## 1.7.0 — 2026-10-27

- **Simpler menus.** The Open menu holds **Open file**, **Open folder** and **Open Disc**;
  **Open Link** and **Download Video** are buttons of their own, on the welcome screen and in
  the controls. The Paste button and "Play from clipboard" are gone (Ctrl+V still plays a
  copied link)
- **Download Video:** the qualities appear as soon as a link is pasted (no more "Get
  qualities"), and **Audio only** saves just the best audio stream, as it is (no re-encoding)
- **BWDIF on discs too:** DVDs and Blu-rays (through libVLC) now deinterlace with BWDIF like
  files, folders and links do, frame for frame the same as FFmpeg's. Auto deinterlaces only
  what the disc flags as interlaced, instead of every disc
- **Audio CDs:** Open Disc plays them from the drive, with the tracks as chapters (Page Up /
  Page Down, the chapters menu), one after the other. A CD rip's folder with its `.cue` sheet
  (or the `.cue` itself) plays the same way. The drive list now also tells empty drives apart
- **Hardware acceleration, redone and working:** since 1.4.0 the app looked at the video
  before mpv had picked its track, so it never actually asked for hardware decoding: HEVC and
  1080p videos decoded on the CPU. Now each video is judged on its own: hardware decoding is on
  by default for HEVC and AV1 at any size and every codec from 1080p up, where the graphics card
  can decode them (the HW badge shows it), and its switch is in the video menu for that video
  only. Videos it doesn't apply to have no switch, and the setting is gone from Settings
- HDR → SDR tonemapping uses **mobius** by default; whoever was on the old default (spline)
  moves to it once
- **Windows: DVDs no longer lag and freeze when they first load.** libVLC opened all of its
  ~250 plugins on the first disc, on the window's thread; it now builds its plugin cache once
  in the background, and starts off that thread. DVDs decode on the CPU (they're standard
  definition), which also keeps the deinterlacer on the plain path
- Linux: libVLC's plugin cache in the Flatpak was always stale, so discs started slower than
  they should; it now matches the installed files

## 1.6.0 — 2026-10-27

- **DVDs and Blu-rays with their menus.** Open → **Open disc** lists your drives (or pick a
  VIDEO_TS / BDMV folder) and plays the disc with its own menus, through libVLC. Click the
  menu buttons or use the arrow keys and Enter. The new **Disc menu** button returns to the
  root menu at any time, and Titles & chapters jumps to any title or chapter
- Store-bought DVDs play (libdvdcss is included). Encrypted Blu-rays (AACS) and Blu-ray Java
  (BD-J) menus aren't supported; those discs fall back to titles and chapters where possible
- Linux: physical DVD and Blu-ray drives work in the Flatpak
- Downloads keep their **metadata**: title, uploader, date, description and chapters, plus the
  thumbnail as cover art
- Downloading the same video again never overwrites or skips it: the resolution is added to
  the name, and a counter if that exists too (`… 720p (1).mkv`)

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
