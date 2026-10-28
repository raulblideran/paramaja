# Paramaja — Build Plan

## Context
You want a modern, portable Windows video player. It should play what VLC plays (HEVC especially), tonemap HDR to SDR as nicely as PotPlayer, stream from magnet links (the top priority), and leave your audio alone. The project folder `paramaja/` is empty, so this is a greenfield build. Once approved, this plan gets copied to `paramaja/PLAN.md` and we tick the boxes as we go.

## Architecture decision: Avalonia + Rust + libmpv (a combination)

| Layer | Tech | Why |
|---|---|---|
| UI shell | **Avalonia 11 / .NET 8 (C#)** | Modern, styleable, fluent-looking UI. Mature desktop tooling. Single-file portable publish for `win-x64`. |
| Playback engine | **libmpv** (`libmpv-2.dll`, ffmpeg-based) | Same codec coverage as VLC (both use ffmpeg). `vo=gpu-next` (libplacebo) gives the best on-the-fly HDR→SDR tonemapping available, Dolby Vision P5/P8 included. Also gives d3d11va hardware decode, libass subtitles, frame-back-step, PNG screenshots and bwdif deinterlacing. |
| Torrent streaming | **Rust `cdylib` wrapping `librqbit`** (`paramaja_torrent.dll`) | librqbit is a fast, modern BitTorrent engine with streaming support: it prioritises pieces sequentially around the playhead. It is exposed to C# through a small C ABI and serves the file over a localhost HTTP range server that mpv plays directly. |

Video embedding: mpv renders into a native child HWND (`--wid`) through Avalonia's `NativeControlHost`. This keeps full `gpu-next` + D3D11 (the libmpv render API doesn't support gpu-next HDR well). Controls sit in a transparent, owned overlay window above the video. This avoids the "airspace" problem, and the controls auto-hide.

Dev loop: everything also runs on Linux (Avalonia and libmpv are cross-platform), so we can develop and test here. Windows is the release target: cross-publish .NET to `win-x64`, build Rust with `cargo-xwin` (MSVC target), and bundle the shinchiro `libmpv-2.dll`.

## Project layout
```
paramaja/
  PLAN.md
  src/Paramaja.App/          Avalonia UI (Views, ViewModels, Styles, Assets/icon)
  src/Paramaja.Core/         Mpv wrapper (P/Invoke), PlayerService, Settings, Torrent interop
  native/paramaja-torrent/         Rust cdylib (librqbit + C ABI + HTTP stream)
  tests/Paramaja.Core.Tests/ xUnit unit + headless-mpv integration tests
  tests/Paramaja.App.Tests/  Avalonia.Headless UI tests
  tests/fixtures/              script to generate test media with ffmpeg
  build/                       publish scripts (portable zip), icon pipeline
```

## Requirement → implementation map
1. **VLC-level formats / HEVC**: libmpv + ffmpeg. `hwdec=auto-safe` (d3d11va for HEVC Main10), with software fallback.
2. **HDR → SDR tonemapping**: `vo=gpu-next`, `tone-mapping=auto` (spline/bt.2446a selectable), `hdr-compute-peak=yes`, `gamut-mapping-mode=perceptual`, Dolby Vision reshaping. The UI has a toggle between *Tonemap to SDR* (default) and *HDR passthrough* (`target-colorspace-hint=yes`) for HDR monitors.
3. **No volume normalisation**: `audio-normalize-downmix=no`, no loudnorm/dynaudnorm filters, `volume=100` passthrough. Tested to stay off.
4. **Subtitles**: embedded tracks (ASS/SRT/PGS/VobSub), external file drag-drop or auto-load, track menu, delay, size and position.
5. **Modern UI**: dark fluent theme, auto-hiding overlay controls, seek bar with hover time, track menus, settings panel, drag-and-drop, and a torrent buffering/peer status panel.
6. **Optional downmix**: setting *Audio output: Original / Stereo downmix*. Stereo sets `audio-channels=stereo` with a proper downmix matrix (center/LFE retained, no normalisation). It is off by default.
7. **Magnet streaming (priority)**: paste a magnet (Ctrl+V or dialog). Rust resolves metadata, lists files, picks the largest video (or the user picks), prioritises sequentially and serves `http://127.0.0.1:<port>/stream/<id>/<file>`, which mpv opens. Seeking re-prioritises pieces. The status panel shows peers, speed and buffer. Data is cached in `./cache`, with a "clear on exit" option.
8. **Uncompressed PNG screenshots**: `screenshot-format=png`, `screenshot-png-compression=0`, high bit depth, saved to `./Screenshots`. One hotkey for the video only and one for video + subs.
9. **Frame step**: `.` steps forward one frame, `,` steps back one frame (`frame-back-step`), and arrows seek ±5s.
10. **Rust + Avalonia**: as above.
11. **Deinterlacing**: `deinterlace=auto` (only interlaced content), using bwdif in software and d3d11vpp with hwdec. There is a manual override toggle (D key).
12. **Hardware acceleration for 4K**: d3d11va zero-copy plus gpu-next. The OSD/stats overlay (key `I`) shows the active hwdec.
13. **Aubergine icon**: SVG → multi-size `.ico` (16–256) for the exe and window.
14. **Portable Windows build**: one folder/zip containing `Paramaja.exe`, `libmpv-2.dll` and `paramaja_torrent.dll`. `settings.json`, `portable_config/`, `cache/` and `Screenshots/` are all stored next to the exe. Nothing goes to AppData or the registry.
15. **Lots of tests**: see Testing below.

> **About the test magnets:** I won't use the two magnet links in your notes (copyrighted films/TV). For the same coverage I'll use legal content: Blender open movies via public WebTorrent magnets (Sintel, Big Buck Bunny, Tears of Steel), legal HEVC/HDR10 and Dolby Vision demo samples, and a fully offline local seeder test. You can of course paste any magnet into the finished player yourself.

## Checklist

### Phase 0 — Scaffolding
- [x] Copy this plan to `paramaja/PLAN.md`, `git init`, `.gitignore`
- [x] Create .NET solution (App, Core, two test projects) and Rust crate
- [x] Script to fetch `libmpv` (Linux: system pkg; Windows: shinchiro `libmpv-2.dll`)
- [x] `tests/fixtures/make-fixtures.sh`: ffmpeg-generated clips. HEVC 10-bit HDR10 (PQ/BT.2020), H.264 SDR, interlaced 1080i, 5.1 and 7.1 audio, `.srt`/`.ass` subs, and a 4K HEVC clip

### Phase 1 — Playback core (`Paramaja.Core`)
- [x] libmpv P/Invoke bindings (create/initialize/command/set/get property/observe/event loop)
- [x] `PlayerService`: open, play/pause, seek, frame-step ±, volume, tracks, events → observable state
- [x] `MpvOptionsBuilder`: maps `Settings` → mpv options (hwdec, gpu-next, tonemap, deinterlace, downmix, screenshots, no-normalise)
- [x] `Settings` load/save to `settings.json` beside the exe (`AppContext.BaseDirectory`), with mpv `portable_config`
- [x] Unit tests for options mapping and settings round-trip

### Phase 2 — UI (`Paramaja.App`)
- [x] Main window with `NativeControlHost` hosting the mpv HWND (X11 window id on Linux)
- [x] Overlay controls window: play/pause, seek bar with hover preview time, volume, time, fullscreen, auto-hide on idle
- [x] Menus: audio track, subtitle track (+ load external), HDR mode, downmix toggle, deinterlace, screenshot
- [x] Open file / drag-drop / command-line arg / recent files
- [x] Keyboard shortcuts (Space, ←/→, `,`/`.`, F, S, Shift+S, D, I, Ctrl+V for magnet)
- [x] Settings panel; dark fluent theme; aubergine accent colour
- [x] Aubergine icon SVG → `.ico`, wired to exe and window

### Phase 3 — Torrent streaming (`native/paramaja-torrent`) ⭐
- [x] Rust crate on `librqbit`: session in `./cache`, add magnet, metadata resolve, file list
- [x] Choose largest video file by default; API to choose another
- [x] Localhost HTTP server with Range support; streaming reader that blocks/prioritises pieces at the read position
- [x] C ABI (JSON in/out): `nt_init`, `nt_add`, `nt_select`, `nt_stats`, `nt_remove`, `nt_shutdown`, `nt_free_string`; magnets also honour `x.pe` peer hints
- [x] C# interop + `TorrentService`; magnet dialog in UI; buffering/peers/speed panel
- [x] Cache cleanup options (on exit / keep)
- [x] Rust unit tests (magnet parsing, file selection, range handling)

### Phase 4 — Feature polish
- [x] HDR: verify tonemap on HDR10 fixture; DV profile 5/8 samples; passthrough toggle
- [x] Downmix: verify 5.1/7.1 → 2ch only when enabled; original channels otherwise
- [x] Screenshots: PNG, compression 0, correct resolution, 10-bit where possible
- [x] Deinterlace auto on 1080i fixture, off on progressive
- [x] Hwdec confirmed on 4K HEVC 10-bit in the Flatpak (Vulkan decoding); Windows d3d11va still to confirm on real hardware

### Phase 5 — Testing (the "lot of testing" part)
- [x] **Core integration tests (headless mpv, `vo=null`/`ao=null` or `vo=image`)**: each fixture opens; codec/colour params are correct; tracks enumerate; frame-back-step changes `estimated-frame-number` by −1; screenshot writes a valid PNG (check the IHDR header and zlib level 0); `audio-params/channel-count` is 6/8 without downmix and 2 with it; `af` stays empty (no normalisation)
- [x] **Torrent integration (offline, deterministic)**: create a `.torrent` from a fixture, seed it with a second librqbit session on localhost, add by magnet, stream via HTTP, check that bytes match and mpv plays it, and that a seek mid-file works
- [x] **Torrent integration (network, opt-in `[Trait("net")]`)**: Sintel/Big Buck Bunny public magnets. Check that metadata resolves, the first frame plays within a timeout, and a seek works
- [x] **UI tests (Avalonia.Headless)**: view-model commands, shortcut bindings, settings panel persists, magnet dialog validation
- [x] `cargo test` + `dotnet test` scripts; CI workflow (GitHub Actions, windows-latest + ubuntu) — optional

### Phase 6 — Windows portable release
- [x] `build/publish-win.sh`: `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`, `cargo build --release --target x86_64-pc-windows-gnu` (MinGW; only system DLL imports), bundle DLLs, zip → `Paramaja-portable-win-x64.zip`
- [x] Verify there are no writes outside the app folder
- [ ] Smoke test on a Windows machine (or VM): 4K HDR HEVC sample, magnet stream, screenshots, downmix

## Verification (end-to-end)
1. `./tests/fixtures/make-fixtures.sh && dotnet test && cargo test` all pass on Linux.
2. Run the app on Linux. Open the HDR10 fixture and check that colours look correct (not washed out) and that the `I` overlay shows gpu-next + tonemapping. Press `,`/`.` to step frames. Press S and check that the PNG lands in `./Screenshots`.
3. Paste the Sintel magnet. Playback should start within seconds, with peers/speed shown, and seeking should work.
4. Toggle downmix on a 5.1 clip and check that the stats show 2ch; toggle it off and they show 6ch.
5. Build the Windows zip, unzip it anywhere on Windows, and run it. `settings.json` should appear next to the exe, and a 4K HEVC clip should play with d3d11va.

### Phase 7 — Linux Flatpak
- [x] Manifest on freedesktop 25.08 (+ rust-stable, dotnet8): builds libmpv with its own ffmpeg (all decoders), libplacebo, libass, LuaJIT, libXpresent
- [x] Desktop entry (incl. `magnet:` handler), metainfo, icon; Flatpak-aware data paths (XDG dirs, ~/Pictures/Paramaja)
- [x] Native library probing: app dir → system loader → dev fallbacks (stops the sandbox loading the host's Homebrew libmpv)
- [x] Packaging tests (manifest/desktop/metainfo/version consistency)
- [x] Verified in the sandbox: HDR10 HEVC playback, 4K HEVC Vulkan hwdec, Sintel magnet streaming with 5.1 audio
- [ ] Flathub: run packaging/flatpak/generate-sources.sh and drop --share=network from the build

### Phase 8 — Fixes & additions from testers (Windows build only for now)
- [x] Deinterlacing: BWDIF by default, Bob as an option; the app inserts the filter itself (never the GPU's), per-file interlace detection
- [x] Controls reappear when the cursor moves, via the OS pointer position as well as mpv's mouse events
- [x] Subtitle size slider shows the font size
- [x] Logarithmic volume (equal dB steps; 50% = −30 dB)
- [x] Magnet streaming: 256 MB in-order lookahead (patched librqbit), 1 MiB HTTP chunks, wait for 5 s of buffer before starting/resuming
- [x] Original audio by default; downmix and AV-receiver passthrough are opt-in and mutually exclusive
- [x] Web URLs, YouTube & other sites (bundled yt-dlp + QuickJS, update button), MEGA file links (decrypted on the fly)
- [x] Buffering quips ("Big fast, many speed", "Bruney", …) in the top bar, on the video and on the welcome screen
- [ ] Live YouTube check on Windows (couldn't run the downloaded yt-dlp here)
- [ ] Linux Flatpak rebuild with these changes (yt-dlp + QuickJS modules) once testers are happy

### Phase 9 — 1.8.0: interlace detection, a lagging Windows laptop, audio, Developer tab
- [x] Interlaced or not, per video: frame flags as before, plus a picture check (FFmpeg idet in a second, headless mpv) for CPU-decoded videos without flags; progressive video never deinterlaced; on/off switch for interlaced videos only, for that video
- [x] Disc engine reports interlacing from libVLC's own detection (its log) instead of "always on in Auto"
- [x] Every engine switch takes over the new engine's state (stale paused icon and deinterlace badge); the pause state is also asked live
- [x] Lag on a Windows laptop with two GPUs and a >60 Hz monitor: audio-clock frame timing for every profile (display sync is opt-in), the high-performance GPU (DXGI) for mpv, position updates throttled, copy-back only while deinterlacing
- [x] Stereo track by default (mpv: file-local aid at on_preloaded; libVLC: matched by title), Left / Right channel for stereo sources; mono downmix removed
- [x] Codec badge; Recent list removed (and no longer stored); Developer tab with the advanced options; Create log
- [x] Menus stay put: the bars don't hide or park while one of their menus is open
- [ ] Friend's laptop: confirm the lag is gone, or read the log from Create log
