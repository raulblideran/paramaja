# Negs Player — Build Plan

## Context
You want a modern, portable Windows video player. It should play what VLC plays (HEVC especially), tonemap HDR to SDR as nicely as PotPlayer, stream from magnet links (the top priority), and leave your audio alone. The project folder `negs-player/` is empty, so this is a greenfield build. Once approved, this plan gets copied to `negs-player/PLAN.md` and we tick the boxes as we go.

## Architecture decision: Avalonia + Rust + libmpv (a combination)

| Layer | Tech | Why |
|---|---|---|
| UI shell | **Avalonia 11 / .NET 8 (C#)** | Modern, styleable, fluent-looking UI. Mature desktop tooling. Single-file portable publish for `win-x64`. |
| Playback engine | **libmpv** (`libmpv-2.dll`, ffmpeg-based) | Same codec coverage as VLC (both use ffmpeg). `vo=gpu-next` (libplacebo) gives the best on-the-fly HDR→SDR tonemapping available, Dolby Vision P5/P8 included. Also gives d3d11va hardware decode, libass subtitles, frame-back-step, PNG screenshots and bwdif deinterlacing. |
| Torrent streaming | **Rust `cdylib` wrapping `librqbit`** (`negs_torrent.dll`) | librqbit is a fast, modern BitTorrent engine with streaming support: it prioritises pieces sequentially around the playhead. It is exposed to C# through a small C ABI and serves the file over a localhost HTTP range server that mpv plays directly. |

Video embedding: mpv renders into a native child HWND (`--wid`) through Avalonia's `NativeControlHost`. This keeps full `gpu-next` + D3D11 (the libmpv render API doesn't support gpu-next HDR well). Controls sit in a transparent, owned overlay window above the video. This avoids the "airspace" problem, and the controls auto-hide.

Dev loop: everything also runs on Linux (Avalonia and libmpv are cross-platform), so we can develop and test here. Windows is the release target: cross-publish .NET to `win-x64`, build Rust with `cargo-xwin` (MSVC target), and bundle the shinchiro `libmpv-2.dll`.

## Project layout
```
negs-player/
  PLAN.md
  src/NegsPlayer.App/          Avalonia UI (Views, ViewModels, Styles, Assets/icon)
  src/NegsPlayer.Core/         Mpv wrapper (P/Invoke), PlayerService, Settings, Torrent interop
  native/negs-torrent/         Rust cdylib (librqbit + C ABI + HTTP stream)
  tests/NegsPlayer.Core.Tests/ xUnit unit + headless-mpv integration tests
  tests/NegsPlayer.App.Tests/  Avalonia.Headless UI tests
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
14. **Portable Windows build**: one folder/zip containing `NegsPlayer.exe`, `libmpv-2.dll` and `negs_torrent.dll`. `settings.json`, `portable_config/`, `cache/` and `Screenshots/` are all stored next to the exe. Nothing goes to AppData or the registry.
15. **Lots of tests**: see Testing below.

> **About the test magnets:** I won't use the two magnet links in your notes (copyrighted films/TV). For the same coverage I'll use legal content: Blender open movies via public WebTorrent magnets (Sintel, Big Buck Bunny, Tears of Steel), legal HEVC/HDR10 and Dolby Vision demo samples, and a fully offline local seeder test. You can of course paste any magnet into the finished player yourself.

## Checklist

### Phase 0 — Scaffolding
- [x] Copy this plan to `negs-player/PLAN.md`, `git init`, `.gitignore`
- [x] Create .NET solution (App, Core, two test projects) and Rust crate
- [ ] Script to fetch `libmpv` (Linux: system pkg; Windows: shinchiro `libmpv-2.dll`)
- [x] `tests/fixtures/make-fixtures.sh`: ffmpeg-generated clips. HEVC 10-bit HDR10 (PQ/BT.2020), H.264 SDR, interlaced 1080i, 5.1 and 7.1 audio, `.srt`/`.ass` subs, and a 4K HEVC clip

### Phase 1 — Playback core (`NegsPlayer.Core`)
- [x] libmpv P/Invoke bindings (create/initialize/command/set/get property/observe/event loop)
- [x] `PlayerService`: open, play/pause, seek, frame-step ±, volume, tracks, events → observable state
- [x] `MpvOptionsBuilder`: maps `Settings` → mpv options (hwdec, gpu-next, tonemap, deinterlace, downmix, screenshots, no-normalise)
- [x] `Settings` load/save to `settings.json` beside the exe (`AppContext.BaseDirectory`), with mpv `portable_config`
- [x] Unit tests for options mapping and settings round-trip

### Phase 2 — UI (`NegsPlayer.App`)
- [ ] Main window with `NativeControlHost` hosting the mpv HWND (X11 window id on Linux)
- [ ] Overlay controls window: play/pause, seek bar with hover preview time, volume, time, fullscreen, auto-hide on idle
- [ ] Menus: audio track, subtitle track (+ load external), HDR mode, downmix toggle, deinterlace, screenshot
- [ ] Open file / drag-drop / command-line arg / recent files
- [ ] Keyboard shortcuts (Space, ←/→, `,`/`.`, F, S, Shift+S, D, I, Ctrl+V for magnet)
- [ ] Settings panel; dark fluent theme; aubergine accent colour
- [ ] Aubergine icon SVG → `.ico`, wired to exe and window

### Phase 3 — Torrent streaming (`native/negs-torrent`) ⭐
- [x] Rust crate on `librqbit`: session in `./cache`, add magnet, metadata resolve, file list
- [x] Choose largest video file by default; API to choose another
- [x] Localhost HTTP server with Range support; streaming reader that blocks/prioritises pieces at the read position
- [x] C ABI (JSON in/out): `nt_init`, `nt_add`, `nt_select`, `nt_stats`, `nt_remove`, `nt_shutdown`, `nt_free_string`; magnets also honour `x.pe` peer hints
- [ ] C# interop + `TorrentService`; magnet dialog in UI; buffering/peers/speed panel
- [ ] Cache cleanup options (on exit / keep)
- [x] Rust unit tests (magnet parsing, file selection, range handling)

### Phase 4 — Feature polish
- [ ] HDR: verify tonemap on HDR10 fixture; DV profile 5/8 samples; passthrough toggle
- [x] Downmix: verify 5.1/7.1 → 2ch only when enabled; original channels otherwise
- [x] Screenshots: PNG, compression 0, correct resolution, 10-bit where possible
- [x] Deinterlace auto on 1080i fixture, off on progressive
- [ ] Hwdec confirmed (`hwdec-current` ≠ `no`) on 4K HEVC

### Phase 5 — Testing (the "lot of testing" part)
- [x] **Core integration tests (headless mpv, `vo=null`/`ao=null` or `vo=image`)**: each fixture opens; codec/colour params are correct; tracks enumerate; frame-back-step changes `estimated-frame-number` by −1; screenshot writes a valid PNG (check the IHDR header and zlib level 0); `audio-params/channel-count` is 6/8 without downmix and 2 with it; `af` stays empty (no normalisation)
- [x] **Torrent integration (offline, deterministic)**: create a `.torrent` from a fixture, seed it with a second librqbit session on localhost, add by magnet, stream via HTTP, check that bytes match and mpv plays it, and that a seek mid-file works
- [x] **Torrent integration (network, opt-in `[Trait("net")]`)**: Sintel/Big Buck Bunny public magnets. Check that metadata resolves, the first frame plays within a timeout, and a seek works
- [ ] **UI tests (Avalonia.Headless)**: view-model commands, shortcut bindings, settings panel persists, magnet dialog validation
- [ ] `cargo test` + `dotnet test` scripts; CI workflow (GitHub Actions, windows-latest + ubuntu) — optional

### Phase 6 — Windows portable release
- [ ] `build/publish-win.sh`: `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`, `cargo xwin build --release --target x86_64-pc-windows-msvc`, bundle DLLs, zip → `NegsPlayer-portable-win-x64.zip`
- [ ] Verify there are no writes outside the app folder
- [ ] Smoke test on a Windows machine (or VM): 4K HDR HEVC sample, magnet stream, screenshots, downmix

## Verification (end-to-end)
1. `./tests/fixtures/make-fixtures.sh && dotnet test && cargo test` all pass on Linux.
2. Run the app on Linux. Open the HDR10 fixture and check that colours look correct (not washed out) and that the `I` overlay shows gpu-next + tonemapping. Press `,`/`.` to step frames. Press S and check that the PNG lands in `./Screenshots`.
3. Paste the Sintel magnet. Playback should start within seconds, with peers/speed shown, and seeking should work.
4. Toggle downmix on a 5.1 clip and check that the stats show 2ch; toggle it off and they show 6ch.
5. Build the Windows zip, unzip it anywhere on Windows, and run it. `settings.json` should appear next to the exe, and a 4K HEVC clip should play with d3d11va.
