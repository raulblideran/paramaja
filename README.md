# Negs Player 🍆

A modern, portable video player. It plays everything mpv/VLC plays (HEVC/H.265 included), tonemaps HDR to SDR on the fly, and streams straight from **magnet links**.

## Using it (Windows, portable)

1. Unzip `NegsPlayer-portable-win-x64.zip` anywhere, such as a USB stick or Downloads.
2. Run `NegsPlayer.exe`. Nothing is installed, and nothing is written to AppData or the registry.
3. Drop a video on the window, press **Ctrl+V** to play a copied magnet link or URL, or use **Open file** / **Magnet link**.

The app keeps all of its data next to the exe:

| Path | What |
|---|---|
| `settings.json` | Your settings (human-readable) |
| `Screenshots/` | Lossless PNG screenshots |
| `cache/` | Torrent data (deleted on exit by default; see Settings → Torrents) |
| `portable_config/` | Optional `mpv.conf` / `input.conf` / shaders for power users, plus mpv's shader cache |

## Features

| | |
|---|---|
| **Formats** | libmpv + FFmpeg: HEVC/H.265 (8/10-bit), H.264, AV1, VP9, VC-1, MPEG-2…; MKV/MP4/TS/M2TS/AVI…; TrueHD, DTS-HD, FLAC, AC3, Opus… |
| **HDR → SDR** | `gpu-next` (libplacebo) tonemapping with scene-adaptive peak detection, perceptual gamut mapping and highlight-detail recovery. HDR10, HLG and Dolby Vision (profiles 5/8). Curves: spline (default), bt.2446a, bt.2390, st2094-40, hable, mobius… **H** switches to HDR passthrough for HDR monitors. |
| **Audio** | Never normalised (no ReplayGain, loudness filters or downmix normalisation). Surround is passed through as-is. **Shift+D** downmixes 5.1/7.1 to stereo, and only when you ask. |
| **Subtitles** | Embedded (ASS/SSA with fonts, SRT, PGS, VobSub) and external files (auto-loaded by name, or drag & drop) |
| **Magnet streaming** | Rust engine ([librqbit](https://github.com/ikatson/rqbit)). It downloads only the file you watch, prioritises pieces around the playhead so seeking works, picks the largest video automatically, and lets you switch between files in the torrent. |
| **Screenshots** | **S**: uncompressed PNG (compression 0) at native resolution, 16-bit for 10-bit/HDR sources. **Shift+S** includes subtitles. |
| **Frame stepping** | **.** goes forward one frame, **,** goes back one frame |
| **Deinterlacing** | Automatic, only for content flagged as interlaced. Uses the GPU deinterlacer with hardware decoding, otherwise bwdif. **D** cycles Auto / On / Off. |
| **Hardware decoding** | D3D11VA zero-copy on Windows (VA-API/Vulkan on Linux) for smooth 4K. **I** shows live stats. |

### Shortcuts

| Key | Action | Key | Action |
|---|---|---|---|
| Space | Play / pause | . / , | Next / previous frame |
| ← / → | Seek ±5 s | Ctrl+← / → | Seek ±60 s |
| ↑ / ↓, wheel | Volume | M | Mute |
| F, Enter, double-click | Fullscreen | Esc | Leave fullscreen |
| S / Shift+S | Screenshot / with subtitles | I | Statistics |
| A / J | Cycle audio / subtitle track | V | Hide subtitles |
| Z / X | Subtitle delay −/+ 0.1 s | H | HDR tonemap ↔ passthrough |
| D | Deinterlace Auto/On/Off | Shift+D | Downmix to stereo |
| Ctrl+O / Ctrl+U / Ctrl+V | Open file / magnet / clipboard | Ctrl+S | Stop |

## Architecture

```
NegsPlayer.App   (C# / Avalonia 11)  UI: welcome screen, overlay controls, dialogs, settings
   │  P/Invoke
   ├── NegsPlayer.Core  (C#)        libmpv bindings, PlayerService, settings, shortcuts
   │     └── libmpv-2.dll           playback, gpu-next HDR, hwdec, subtitles, screenshots
   └── negs_torrent.dll (Rust)      librqbit session + localhost HTTP Range server + C ABI
```

mpv renders into a native child window (`--wid`), so the full gpu-next/D3D11 pipeline is kept. The controls are transparent overlay windows floated above the video.

Magnet flow: `nt_add(magnet)` resolves metadata and selects the largest video. mpv then opens `http://127.0.0.1:<port>/stream/<id>/<file>`, and each HTTP range request becomes a librqbit `FileStream` that prioritises the pieces it needs.

## Building

Requirements: .NET 8 SDK, Rust (with the `x86_64-pc-windows-gnu` target and MinGW for Windows cross-builds), ffmpeg + mpv/libmpv (for tests), 7-Zip, ImageMagick (icon only).

```bash
./build/test.sh              # fixtures + Rust + .NET tests (add --net for the real-swarm test)
./build/publish-win.sh       # → dist/NegsPlayer-portable-win-x64.zip
./build/fetch-libs.sh linux-x64 && dotnet run --project src/NegsPlayer.App   # run on Linux
./build/make-icon.sh         # re-render the aubergine icon from Assets/negs.svg
```

Set `NEGS_DEBUG=1` to write a verbose mpv log to `logs/mpv.log`.

### Tests

* **Rust** (`native/negs-torrent`): unit tests for magnet parsing, file selection and HTTP ranges. There's an offline end-to-end test (a local seeder → magnet with `x.pe` → HTTP range streaming → mpv playback) and an opt-in real-swarm test using Blender's *Sintel* (CC-BY).
* **Core** (`tests/NegsPlayer.Core.Tests`): every generated mpv option is checked against real libmpv. Headless playback of generated fixtures checks:
  * HEVC Main10 HDR10 detection and 4K HEVC playback
  * 5.1/7.1 passthrough vs downmix, and no normalisation
  * exact frame step and back-step
  * screenshots that really are uncompressed PNGs (stored deflate blocks), 16-bit for HDR
  * auto-deinterlacing that kicks in only on 1080i
  * embedded/external subtitles and key forwarding

  The full C# → Rust → mpv path is also tested through a local seeder.
* **App** (`tests/NegsPlayer.App.Tests`): view-model tests (shortcuts, magnet flow, settings persistence, auto-hide, badges) and Avalonia headless UI tests (controls bar, seek bar, settings, magnet dialog, welcome screen).

Test media is generated by `tests/fixtures/make-fixtures.sh`, so no copyrighted content is needed.

## Credits

Built on [mpv](https://mpv.io) (libmpv, LGPL/GPL), [FFmpeg](https://ffmpeg.org), [libplacebo](https://code.videolan.org/videolan/libplacebo), [librqbit](https://github.com/ikatson/rqbit) (Apache-2.0), [Avalonia](https://avaloniaui.net) (MIT) and [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT). Icons are from [Material Design Icons](https://pictogrammers.com) (Apache-2.0). Windows libmpv builds come from [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake).

The shinchiro libmpv build is GPL-licensed, so if you redistribute the zip, the combined distribution falls under the GPL.
