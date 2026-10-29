# Paramaja 🍆

A modern, portable video player. It plays everything mpv/VLC plays (HEVC/H.265 included), tonemaps HDR to SDR on the fly, and streams straight from **magnet links**.

## Using it (Windows, portable)

1. Unzip `Paramaja-<version>-portable-win-x64.zip` anywhere, such as a USB stick or Downloads. To update, unzip a newer version over the old `Paramaja` folder: settings and screenshots are kept. The version is shown on the welcome screen and under Settings → About.
2. Run `Paramaja.exe`. Nothing is installed, and nothing is written to AppData or the registry.
3. Drop a video on the window, press **Ctrl+V** to play a copied link (magnet, YouTube, MEGA or any video URL), or use **Open** (Open file, Open folder, Open Disc) / **Open Link**.

The app keeps all of its data next to the exe:

| Path | What |
|---|---|
| `settings.json` | Your settings (human-readable) |
| `Screenshots/` | Lossless PNG screenshots |
| `cache/` | Torrent data (deleted on exit by default; see Settings → Torrents) |
| `portable_config/` | Optional `mpv.conf` / `input.conf` / shaders for power users, plus mpv's shader cache |

## Using it (Linux, as a Flatpak)

```sh
flatpak install --user paramaja-1.3.0.flatpak
flatpak run io.github.raulblideran.Paramaja      # or launch it from the app menu
```

The bundle carries its own libmpv, built with an ffmpeg that has every decoder. The runtime's ffmpeg leaves out HEVC, H.264, AAC and AC-3. The app also registers itself as the handler for `magnet:` links, so clicking one in a browser opens it in Paramaja.

A Flatpak can't write next to its own executable, so on Linux the data lives in the standard places:

| Path | What |
|---|---|
| `~/.var/app/io.github.raulblideran.Paramaja/config/settings.json` | Settings |
| `~/.var/app/io.github.raulblideran.Paramaja/config/portable_config/` | Optional `mpv.conf` / `input.conf` / shaders |
| `~/.var/app/io.github.raulblideran.Paramaja/cache/torrents/` | Torrent data (deleted on exit by default) |
| `~/Pictures/Paramaja/` | Screenshots |
| `~/Downloads/` | Downloaded videos |

It can read physical DVDs and Blu-rays from the drive (`--device=all`), and every file on the system (`--filesystem=host:ro`), because the file chooser portal only hands out one file at a time, and subtitles next to a video are auto-loaded by name. Drives under `/var/mnt` on Bazzite and other Atomic desktops aren't covered by `host`. Add them with:

```sh
flatpak override --user --filesystem=/var/mnt:ro io.github.raulblideran.Paramaja
```

### Building the Flatpak yourself

```sh
flatpak install --user flathub org.flatpak.Builder org.freedesktop.Sdk//25.08 \
    org.freedesktop.Sdk.Extension.rust-stable//25.08 org.freedesktop.Sdk.Extension.dotnet8//25.08

./build/publish-flatpak.sh     # builds, installs for your user, writes dist/paramaja-<version>.flatpak
```

The first build compiles FFmpeg, libplacebo, libass, LuaJIT and mpv. Later rebuilds only redo the app.

The bundle is created with `--runtime-repo`, so it installs on machines that don't already have the 25.08 runtime.

The manifest builds the Rust and .NET parts with `--share=network` so cargo and NuGet can download packages. That's fine locally but not allowed on Flathub. `packaging/flatpak/generate-sources.sh` writes the offline package lists that replace it.

## Features

| | |
|---|---|
| **Formats** | libmpv + FFmpeg: HEVC/H.265 (8/10-bit), H.264, AV1, VP9, VC-1, MPEG-2…; MKV/MP4/TS/M2TS/AVI (uncompressed too)/VOB…; TrueHD, DTS-HD, FLAC, AC3, Opus… |
| **Folders & discs** | **Open → Open folder** (Ctrl+Shift+O) or drop a folder: its videos play in order. **Open → Open Disc** plays a DVD or Blu-ray from a drive, a `VIDEO_TS` / `BDMV` folder or a disc image (`.iso`) **with its menus**, through [libVLC](https://www.videolan.org/vlc/libvlc.html) (mpv has no disc menu support). Click the menu buttons, or use the arrow keys and Enter; the **Disc menu** button returns to the root menu at any time, and the **Titles & chapters** menu jumps to any title or chapter. Store-bought DVDs play (libdvdcss); encrypted (AACS) Blu-rays and Java (BD-J) Blu-ray menus don't. |
| **Audio CDs** | **Open Disc** plays an Audio CD from its drive, through libVLC, with the tracks as chapters (Page Up / Page Down, the chapters menu), one after the other. A CD rip plays the same way from its `.cue` sheet: open the sheet, drop it, or pick the rip's folder. So does a CD image (`.bin` / `.cue`). Sheets as rippers like EAC write them (naming the `.wav` that's now a `.flac`, a Windows sub-folder, other letter case, gaps appended to the previous track) are read by Paramaja and played as one timeline of the files really there. |
| **SACD & DSD** | DSD files (`.dsf`, `.dff`) play like any other music. **SACD images** (the `.iso` a disc ripper makes of an SACD) play with Paramaja's own SACD reader: the stereo area by default, the multichannel one (5.1) from the Titles & chapters menu, with the tracks as chapters and the disc's titles. Plain DSD and DST-compressed discs are both read; FFmpeg decodes the audio. |
| **HDR** | Automatic: on a monitor with HDR switched on in Windows, HDR video is passed through untouched; on an SDR monitor it is tonemapped with `gpu-next` (libplacebo): scene-adaptive peak detection, perceptual gamut mapping and highlight-detail recovery. HDR10, HLG and Dolby Vision (profiles 5/8). The curve (mobius by default, spline, bt.2446a, bt.2390, st2094-40, hable…) is in Settings → Developer. **H** switches by hand; the HDR mode is in Settings → Developer. On Linux the app is an X11 client, which can't output HDR, so it always tonemaps. |
| **Audio** | The original audio stream plays as-is by default: no ReplayGain or downmix normalisation. Where a video or disc has several audio tracks, a stereo one plays by default (within your preferred language, if you set one). For a stereo source, the audio menu can play just its **left or right channel** on both speakers, for that video. Optional, all off by default: **Shift+D** downmixes 5.1/7.1 to stereo (surround sources only), and volume normalization (ffmpeg's dynamic normalizer) evens out loud and quiet parts. Passthrough (Settings → Developer) sends Dolby/DTS bitstreams untouched to an AV receiver. 5.1 plays as 5.1, discs included. The volume slider is logarithmic: it moves in equal steps of loudness, so 50% is −30 dB. |
| **Subtitles** | Embedded (ASS/SSA with fonts, SRT, PGS, VobSub) and external files (auto-loaded by name, or drag & drop). The size slider shows the resulting font size. |
| **Magnet streaming** | Rust engine ([librqbit](https://github.com/ikatson/rqbit)). It downloads only the file you watch, fetching pieces in order up to 256 MB ahead of the playhead (about 25 s of a 4K HDR remux; librqbit's default is 32 MB). Before starting or after a stall it waits for 5 s of video, instead of stuttering through a thin buffer. It picks the largest video automatically. A torrent with several videos or songs is a playlist: **Previous** / **Next** in the controls (and the list in the top bar) move between them, and each plays on into the next; an album starts at its first track. |
| **Web links** | Direct video URLs, plus YouTube and ~1800 other sites through the bundled [yt-dlp](https://github.com/yt-dlp/yt-dlp) and [QuickJS-NG](https://github.com/quickjs-ng/quickjs), the JavaScript runtime YouTube needs. Settings → Playback → **Update YouTube support** updates yt-dlp when a site changes. Settings → Developer → **Streaming quality** caps the resolution streamed (best available by default); while a site's video plays, the video menu lists the qualities it offers and switches to one at once, for that video. |
| **Downloads** | **Download Video** (welcome screen, the controls, or the button at the top while a web video plays): paste a link and the qualities the site offers (2160p, 1080p, 720p…) appear by themselves. Pick one and it's saved to your Downloads folder with the best audio merged in (one MKV, no re-encoding, by the bundled ffmpeg), or pick **Audio only** for the best audio stream on its own (Opus, AAC… as it is). Title, uploader, date, description, chapters and thumbnail as cover art are kept. Downloading the same video again never overwrites: the resolution is added to the name, then a counter (`… 720p (1).mkv`). |
| **MEGA** | `mega.nz` file links (new and legacy formats) are fetched from MEGA and decrypted on the fly, so seeking works. Folder links aren't supported yet. MEGA's free transfer quota applies. |
| **Screenshots** | **S**: uncompressed PNG (compression 0) at native resolution, 16-bit for 10-bit/HDR sources. **Shift+S** includes subtitles. |
| **Frame stepping** | **.** goes forward one frame, **,** goes back one frame |
| **Deinterlacing** | Automatic, for interlaced videos only, from every source: files, folders, links and discs. A video counts as interlaced when its frames are flagged so, or, for videos the CPU decodes, when FFmpeg's `idet` sees it in the picture (tape captures saved as MJPEG or lossless AVIs carry no flags): a second, headless mpv checks a few hundred frames beside playback and needs a clear majority. **BWDIF** (motion-adaptive), one frame per field (50i becomes 50p). Discs play through libVLC, which has no BWDIF of its own: Paramaja's libVLC carries a port of FFmpeg's, which gives exactly the same frames. Settings → Developer has an on/off switch while an interlaced video plays, for that video; progressive videos have none. **D** flips it. |
| **Badges** | The controls show the codec (H.264, HEVC, MPEG-2…), resolution, HDR, hardware or software decoding, the audio channels and the deinterlacer. |
| **Rendering** | In Settings → Developer. Balanced (default) is nearly as sharp as High at a fraction of the cost. Auto uses mpv's best scalers below 4K and Balanced for 4K. Scaling a 4K picture costs ~5× more than 1080p: measured 2.9 ms vs 0.6 ms per frame on a fast GPU, so a weaker one runs out of time only at 4K. If frames still drop, Auto switches to Fast (about VLC's cost). High, Balanced and Fast can also be chosen directly. |
| **Hardware decoding** | Decided for each video: on for HEVC (H.265) and AV1 at any size and for every codec from 1080p up, where the graphics card can decode them (D3D11VA zero-copy on Windows, VA-API/Vulkan on Linux) for smooth 4K. Its switch then appears in Settings → Developer, for that video only. Smaller videos, and anything the card can't decode, play in software with no switch. DVDs decode in software, Blu-rays on the GPU. On Windows laptops with two GPUs, playback runs on the high-performance one (Settings → Developer). Frames are timed by the audio clock, as mpv does by default, which no refresh rate can upset; syncing to the display is an option in Settings → Developer. **I** shows live stats. |
| **Picture** | The video menu can **resize** the picture to 4:3 (any video that isn't 4:3) or **crop** it to 16:9 (any video that isn't 16:9), for that video. |
| **Developer tab** | Settings → Developer holds the advanced options (this video's deinterlacing and hardware decoding switches, rendering quality, frame timing, HDR mode and tonemapping, audio passthrough, streaming quality, GPU choice) and the troubleshooting tools: **Create log** saves every second's playback statistics since the video started (frames dropped and late, GPU time per frame, CPU use, refresh rates), the machine and how the video is played to a file in the `logs` folder, for benchmarking or for whoever is helping. Statistics overlay and Copy diagnostics are there too. |

### Shortcuts

| Key | Action | Key | Action |
|---|---|---|---|
| Space | Play / pause | . / , | Next / previous frame |
| Page Down / Page Up | Next / previous chapter | | |
| ← / → | Seek ±5 s | Ctrl+← / → | Seek ±60 s |
| ↑ / ↓, wheel | Volume | M | Mute |
| F, Enter, double-click | Fullscreen | Esc | Leave fullscreen |
| S / Shift+S | Screenshot / with subtitles | I | Statistics |
| A / J | Cycle audio / subtitle track | V | Hide subtitles |
| Z / X | Subtitle timing −/+ 0.1 s (the total shows on screen) | H | HDR tonemap ↔ passthrough (overrides Auto) |
| D | Deinterlacing on / off (interlaced videos) | Shift+D | Downmix to stereo (5.1 / 7.1) |
| Ctrl+O / Ctrl+Shift+O | Open file / folder | Ctrl+U / Ctrl+V | Open link / play a copied link |
| Ctrl+S | Stop | | |
| Ctrl+I | Copy diagnostics (while the problem video plays) | | |

## Architecture

```
Paramaja.App   (C# / Avalonia 11)  UI: welcome screen, overlay controls, dialogs, settings
   │  P/Invoke
   ├── Paramaja.Core  (C#)        libmpv bindings, PlayerService, settings, shortcuts
   │     └── libmpv-2.dll           playback, gpu-next HDR, hwdec, subtitles, screenshots
   │     └── libvlc/                DVDs and Blu-rays with their menus (VlcDiscPlayer)
   ├── paramaja_torrent.dll (Rust)      librqbit session, MEGA decryption, localhost HTTP Range server, C ABI
   └── yt-dlp.exe + qjs.exe         YouTube & other sites, called by mpv's ytdl hook
         └── ffmpeg.exe + ffprobe.exe   merge downloads and embed their metadata and cover art
```

mpv renders into a native child window (`--wid`), so the full gpu-next/D3D11 pipeline is kept. Discs play through libVLC instead, into a second native window shown only while a disc plays; both engines sit behind the same `IPlayer` interface, so the controls drive either. The controls are transparent overlay windows floated above the video.

Magnet flow: `nt_add(magnet)` resolves metadata and selects the largest video. mpv then opens `http://127.0.0.1:<port>/stream/<id>/<file>`, and each HTTP range request becomes a librqbit `FileStream` that prioritises the pieces it needs.

## Building

Requirements: .NET 8 SDK, Rust (with the `x86_64-pc-windows-gnu` target and MinGW for Windows cross-builds), ffmpeg + mpv/libmpv (for tests), 7-Zip, ImageMagick (icon only).

```bash
./build/test.sh              # fixtures + Rust + .NET tests (add --net for the real-swarm test)
./build/bump-version.sh 1.2.0 "What changed"   # sets the version in the project, CHANGELOG.md and metainfo
./build/publish-win.sh       # → dist/Paramaja-<version>-portable-win-x64.zip
./build/publish-flatpak.sh   # → dist/paramaja-<version>.flatpak (see above)
./build/fetch-libs.sh linux-x64 && dotnet run --project src/Paramaja.App   # run on Linux
./build/make-icon.sh         # re-render the aubergine icon from Assets/paramaja.svg
```

### Releases

Pushing a version tag publishes a GitHub Release: `.github/workflows/release.yml` builds the
Windows zip and the Flatpak from that commit, with the version's CHANGELOG.md section as the
notes (`build/release-notes.sh`). The release goes up as soon as the Windows zip is ready, a
few minutes in, and the Flatpak is attached when its longer build finishes. The tag must
match the project version.

```bash
./build/bump-version.sh 1.3.3 "What changed"   # then list the changes in CHANGELOG.md
git commit -am "1.3.3: what changed"
git tag v1.3.3 && git push origin main v1.3.3
```

Set `PARAMAJA_DEBUG=1` to write a verbose mpv log to `logs/mpv.log`.

### Tests

* **Rust** (`native/paramaja-torrent`): unit tests for magnet parsing, file selection and HTTP ranges. There's an offline end-to-end test (a local seeder → magnet with `x.pe` → HTTP range streaming → mpv playback) and an opt-in real-swarm test using Blender's *Sintel* (CC-BY).
* **Core** (`tests/Paramaja.Core.Tests`): every generated mpv option is checked against real libmpv. Headless playback of generated fixtures checks:
  * HEVC Main10 HDR10 detection and 4K HEVC playback
  * 5.1/7.1 passthrough vs downmix, and no normalisation
  * exact frame step and back-step
  * screenshots that really are uncompressed PNGs (stored deflate blocks), 16-bit for HDR
  * auto-deinterlacing for flagged 1080i and for unflagged interlaced pictures (idet), never for progressive video
  * a stereo track picked by default, and a stereo source's left or right channel on both speakers
  * embedded/external subtitles and key forwarding

  The full C# → Rust → mpv path is also tested through a local seeder.
* **App** (`tests/Paramaja.App.Tests`): view-model tests (shortcuts, magnet flow, settings persistence, auto-hide, badges) and Avalonia headless UI tests (controls bar, seek bar, settings, magnet dialog, welcome screen).

Test media is generated by `tests/fixtures/make-fixtures.sh`, so no copyrighted content is needed.

## Credits

`native/paramaja-torrent/vendor/librqbit` is librqbit 9.0.1 with one small patch that makes the stream lookahead configurable (`vendor/PARAMAJA-librqbit.patch`).

Built on [mpv](https://mpv.io) (libmpv, LGPL/GPL), [FFmpeg](https://ffmpeg.org), [libplacebo](https://code.videolan.org/videolan/libplacebo), [librqbit](https://github.com/ikatson/rqbit) (Apache-2.0), [Avalonia](https://avaloniaui.net) (MIT) and [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT), [yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense) and [QuickJS-NG](https://github.com/quickjs-ng/quickjs) (MIT). Icons are from [Material Design Icons](https://pictogrammers.com) (Apache-2.0). Windows libmpv builds come from [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake).

Both builds bundle a GPL libmpv, so the project is licensed GPL-3.0-or-later (see `LICENSE`).
