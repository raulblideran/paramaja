using System.Globalization;
using Paramaja.Core.Settings;

namespace Paramaja.Core.Playback;

/// <summary>
/// Translates <see cref="PlayerSettings"/> into mpv options. Grouped so the player can
/// re-apply a single group live (e.g. toggling downmix) without restarting playback.
/// </summary>
public static class MpvOptionsBuilder
{
    public static IReadOnlyList<KeyValuePair<string, string>> Build(
        PlayerSettings s, PortablePaths paths, long? windowId = null, YtDlp? ytdlp = null)
    {
        var o = new List<KeyValuePair<string, string>>();
        o.AddRange(Core(paths, windowId));
        o.AddRange(Ytdl(ytdlp ?? new YtDlp(AppContext.BaseDirectory), s.StreamMaxHeight));
        o.AddRange(Video(s));
        o.AddRange(Timing(s));
        o.AddRange(Scaling(InitialProfile(s.RenderQuality)));
        o.AddRange(Hdr(s, InitialProfile(s.RenderQuality)));
        o.AddRange(Deinterlace());
        o.AddRange(Audio(s));
        o.AddRange(Subtitles(s));
        o.AddRange(Screenshots(paths));
        o.AddRange(Network());
        return o;
    }

    /// <summary>
    /// Nice-to-haves that older libmpv builds don't know (directory-filter-types is mpv 0.38+),
    /// or that only some builds have (d3d11-adapter is Windows only). A distro's libmpv may be
    /// older than the bundled one, and playback shouldn't fail over these.
    /// </summary>
    public static readonly IReadOnlySet<string> OptionalOptions =
        new HashSet<string> { "directory-filter-types", "display-fps-override", "d3d11-adapter", "audio-exts" };

    /// <summary>The audio files an opened folder plays (mpv 0.41's list, plus DSD).</summary>
    public const string AudioExtensions = "aac,ac3,aiff,ape,au,dts,eac3,flac,m4a,mka,mp3,oga,ogg,ogm,opus,thd,wav,wma,wv,dsf,dff";

    private static KeyValuePair<string, string> P(string k, string v) => new(k, v);
    private static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    public static IEnumerable<KeyValuePair<string, string>> Core(PortablePaths paths, long? windowId)
    {
        // Portable: mpv reads/writes only inside the app folder.
        yield return P("config", "yes");
        yield return P("config-dir", paths.MpvConfig);
        yield return P("load-scripts", "no");
        // mpv's caches default to %LOCALAPPDATA%; keep them in the portable folder too.
        var cache = Path.Combine(paths.MpvConfig, "cache");
        yield return P("gpu-shader-cache-dir", cache);
        yield return P("icc-cache-dir", cache);
        yield return P("demuxer-cache-dir", cache);
        yield return P("watch-later-dir", Path.Combine(paths.MpvConfig, "watch_later"));
        if (windowId is { } wid)
        {
            yield return P("wid", wid.ToString(CultureInfo.InvariantCulture));
            // On Linux the host window is an X11 (XWayland) window; mpv would otherwise
            // pick a Wayland surface of its own and ignore the embedding handle.
            if (OperatingSystem.IsLinux()) yield return P("gpu-context", "x11vk");
        }
        // Our own UI handles input & controls; mpv's built-in OSC and bindings are off.
        yield return P("osc", "no");
        yield return P("input-default-bindings", "no");
        yield return P("input-vo-keyboard", "yes");
        yield return P("osd-bar", "no");
        yield return P("osd-level", "1");
        yield return P("osd-font-size", "36");
        yield return P("idle", "yes");
        yield return P("keep-open", "yes");
        yield return P("force-window", windowId is null ? "no" : "yes");
        // The app hides the pointer together with its controls (see PlayerService.SetCursorHidden).
        yield return P("cursor-autohide", "no");
        // Precise seeks are required for exact frame stepping.
        yield return P("hr-seek", "yes");
        yield return P("hr-seek-framedrop", "no");
        yield return P("terminal", "no");
        // Drops are handled by the app (magnet text, subtitles), not by mpv's video window.
        yield return P("drag-and-drop", "no");
        // An opened folder plays as a playlist of its videos, without cover art or subtitles in between.
        yield return P("directory-filter-types", "video,audio");
        // mpv's own list of audio files lacks DSD (.dsf, .dff), which it plays.
        yield return P("audio-exts", AudioExtensions);
    }

    /// <summary>YouTube and other sites: mpv's ytdl hook hands page URLs to yt-dlp.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Ytdl(YtDlp ytdlp, int streamMaxHeight = 0)
    {
        yield return P("ytdl", "yes");
        yield return P("ytdl-format", YtDlp.Format(streamMaxHeight));
        if (ytdlp.BundledPath is { } exe) yield return P("script-opts", $"ytdl_hook-ytdl_path={Quote(exe)}");
        // YouTube needs a JavaScript runtime; yt-dlp only enables Deno on its own.
        if (ytdlp.BundledJsRuntime is { } js) yield return P("ytdl-raw-options", $"js-runtimes={Quote("quickjs:" + js)}");
    }

    /// <summary>
    /// mpv's length-prefixed quoting (<c>%N%value</c>), so a path containing commas or
    /// '=' stays one value in a key/value list. N counts UTF-8 bytes.
    /// </summary>
    internal static string Quote(string value) => $"%{System.Text.Encoding.UTF8.GetByteCount(value)}%{value}";

    public static IEnumerable<KeyValuePair<string, string>> Video(PlayerSettings s)
    {
        yield return P("vo", "gpu-next");
        // D3D11 on Windows gives zero-copy d3d11va decode for 4K HEVC/AV1.
        yield return P("gpu-api", OperatingSystem.IsWindows() ? "d3d11" : "auto");
        // Off until a file loads: PlayerService decides per file (see UseHardwareDecoding).
        yield return P("hwdec", "no");
        yield return P("interpolation", "no");
        yield return P("deband", "no");
    }

    /// <summary>
    /// Whether hardware decoding would help: HEVC and AV1 at any size, and any codec from 1080p
    /// up (1920-wide scope films such as 1920×800 included). Smaller videos decode in software,
    /// which is cheap for them and keeps filters such as deinterlacing simple. Whether the GPU
    /// can actually decode the video is found out when it starts (PlayerService).
    /// </summary>
    public static bool UseHardwareDecoding(string? codec, int? width, int? height) =>
        codec is "hevc" or "av1" || width >= 1900 || height >= 1060;

    /// <summary>
    /// The channel layout to output. The stereo downmix applies only to surround sources.
    /// Passthrough leaves the bitstream alone.
    /// </summary>
    public static string AudioChannels(PlayerSettings s, int? sourceChannels) =>
        !s.AudioPassthrough && s.DownmixToStereo && sourceChannels is null or > 2 ? "stereo" : "auto";

    /// <summary>The audio device is the player's alone (WASAPI exclusive): for passthrough, or by the exclusive switch.</summary>
    public static bool AudioExclusive(PlayerSettings s) => s.AudioPassthrough || s.ExclusiveAudio;

    /// <summary>
    /// The audio filters: one channel of a stereo source on both speakers, then the normalizer.
    /// A bitstream sent to a receiver can't be filtered.
    /// </summary>
    public static string AudioFilters(PlayerSettings s, int? sourceChannels, StereoChannel channel = StereoChannel.Both)
    {
        if (s.AudioPassthrough) return "";
        var filters = new List<string>();
        if (sourceChannels == 2 && channel != StereoChannel.Both) filters.Add(ChannelFilter(channel));
        if (s.NormalizeAudio) filters.Add(NormalizeFilter);
        return string.Join(",", filters);
    }

    /// <summary>The left (or right) channel to both outputs: c0 is left, c1 right.</summary>
    internal static string ChannelFilter(StereoChannel channel) =>
        channel == StereoChannel.Left ? "lavfi=[pan=stereo|c0=c0|c1=c0]" : "lavfi=[pan=stereo|c0=c1|c1=c1]";

    /// <summary>
    /// The audio track to play when a file has several: a stereo one (the default-flagged one
    /// first) where there is one. With preferred languages, only the first of them the file has
    /// counts, so the language still comes first. Null leaves mpv's own choice.
    /// </summary>
    public static int? StereoTrack(IReadOnlyList<TrackInfo> tracks, string? preferredLanguages = null)
    {
        var audio = tracks.Where(t => t.Type == TrackType.Audio && !t.External).ToList();
        if (audio.Count < 2) return null;
        foreach (var lang in (preferredLanguages ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var inLanguage = audio.Where(t => string.Equals(t.Language, lang, StringComparison.OrdinalIgnoreCase)).ToList();
            if (inLanguage.Count == 0) continue;
            audio = inLanguage;
            break;
        }
        return audio.Where(t => t.Channels == 2).OrderByDescending(t => t.Default).ThenBy(t => t.Id).FirstOrDefault()?.Id;
    }

    /// <summary>ffmpeg's dynamic normalizer, with a short window so it reacts within a couple of seconds.</summary>
    public const string NormalizeFilter = "lavfi=[dynaudnorm=f=250:g=15:p=0.9]";

    /// <summary>Videos at least this tall count as 4K for Auto rendering (covers 3840×1600 scope too).</summary>
    public const int UhdHeight = 1500;

    public static RenderProfile InitialProfile(RenderQuality q) => q switch
    {
        RenderQuality.Fast => RenderProfile.Fast,
        RenderQuality.Balanced => RenderProfile.Balanced,
        _ => RenderProfile.High,
    };

    /// <summary>What Auto uses for a video of this size before any frames have dropped.</summary>
    public static RenderProfile AutoProfileFor(int? videoWidth, int? videoHeight) =>
        videoHeight >= UhdHeight || videoWidth >= 3000 ? RenderProfile.Balanced : RenderProfile.High;

    /// <summary>
    /// The cost of each profile scales with the video's own resolution: chroma is upscaled
    /// to it, so an EWA chroma scaler on a 4K video does 4× the work of 1080p. That is
    /// why High is fine at 1080p and too much for many GPUs at 4K.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> Scaling(RenderProfile p)
    {
        var (scale, cscale, dscale) = p switch
        {
            RenderProfile.High => ("ewa_lanczossharp", "ewa_lanczossharp", "mitchell"),
            RenderProfile.Balanced => ("lanczos", "bilinear", "hermite"),
            _ => ("bilinear", "bilinear", "bilinear"),
        };
        yield return P("scale", scale);
        yield return P("cscale", cscale);
        yield return P("dscale", dscale);
        yield return P("correct-downscaling", p == RenderProfile.High ? "yes" : "no");
        yield return P("linear-downscaling", p == RenderProfile.High ? "yes" : "no");
        yield return P("sigmoid-upscaling", p == RenderProfile.High ? "yes" : "no");
        yield return P("dither-depth", p == RenderProfile.Fast ? "no" : "auto");
    }

    /// <summary>
    /// How frames are timed: by the audio clock (mpv's default) unless the user asks to sync to
    /// the display. display-resample retimes audio to the display's refresh, which needs that rate
    /// right: with a wrong one (a fixed 60 on a high-refresh monitor, a laptop's two GPUs) it
    /// stuttered, while the audio clock is immune to it.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> Timing(PlayerSettings s)
    {
        yield return P("video-sync", s.SyncToDisplay ? "display-resample" : "audio");
        yield return P("display-fps-override", F(s.SyncToDisplay ? s.DisplayFps : 0));
    }

    /// <param name="displayIsHdr">The window is on a display in HDR mode; decides what Auto does.</param>
    public static IEnumerable<KeyValuePair<string, string>> Hdr(PlayerSettings s, RenderProfile profile = RenderProfile.High,
        bool displayIsHdr = false)
    {
        if (s.EffectiveHdrMode(displayIsHdr) == HdrMode.Passthrough)
        {
            // Let the display do the work; mpv switches the swapchain to HDR when content is HDR.
            yield return P("target-colorspace-hint", "yes");
            yield return P("tone-mapping", "auto");
            yield break;
        }
        yield return P("target-colorspace-hint", "no");
        yield return P("target-trc", "auto");
        yield return P("target-prim", "auto");
        yield return P("tone-mapping", s.ToneMapping);
        // Contrast recovery (a full-resolution blur) and scene-by-scene peak detection are the
        // expensive parts of tonemapping. Balanced drops the first, Fast both.
        yield return P("hdr-compute-peak", s.HdrDynamicPeak && profile != RenderProfile.Fast ? "yes" : "no");
        yield return P("hdr-peak-percentile", "99.995");
        yield return P("hdr-contrast-recovery", F(profile == RenderProfile.High ? s.HdrContrastRecovery : 0));
        yield return P("gamut-mapping-mode", s.GamutMapping);
    }

    /// <summary>
    /// The crop (mpv's video-crop, "WxH+X+Y" in the source's pixels) that leaves the middle of the
    /// picture at <paramref name="target"/> width over height: the sides of a wider picture, the
    /// top and bottom of a narrower one. Null where the size isn't known or the shape already fits.
    /// </summary>
    public static string? CropTo(int? width, int? height, double? displayAspect, double target)
    {
        if (width is not ({ } w and > 0) || height is not ({ } h and > 0) || displayAspect is not ({ } dar and > 0)) return null;
        if (Math.Abs(dar / target - 1) < 0.01) return null;
        int cw = w, ch = h;
        if (dar > target) cw = Even(w * target / dar);
        else ch = Even(h * dar / target);
        return $"{cw}x{ch}+{(w - cw) / 2}+{(h - ch) / 2}";
    }

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);

    /// <summary>
    /// mpv's top margin for its messages that clears the top bar: <paramref name="fraction"/> of the
    /// window's height is covered by it, and mpv counts in scaled pixels, 720 to the window's height.
    /// </summary>
    public static long OsdMarginBelow(double fraction, long baseMargin) =>
        baseMargin + (long)Math.Round(Math.Clamp(fraction, 0, 1) * 720);

    public static IEnumerable<KeyValuePair<string, string>> Deinterlace()
    {
        // mpv's own deinterlace option would pick the GPU's deinterlacer when hardware
        // decoding, whatever we asked for. PlayerService inserts the chosen filter itself.
        yield return P("deinterlace", "no");
    }

    public const string DeinterlaceLabel = "@paramaja-deint";

    /// <summary>BWDIF, motion adaptive, one frame per field (50i → 50p).</summary>
    public const string DeinterlaceFilter = $"{DeinterlaceLabel}:bwdif=mode=send_field";

    /// <summary>
    /// The field order is the frames' own where they're flagged. Frames without flags (found
    /// interlaced by the picture check) would be taken as top field first, so they get the order
    /// that check saw.
    /// </summary>
    public static string DeinterlaceFilterFor(FieldOrder? order) => order switch
    {
        FieldOrder.TopFirst => DeinterlaceFilter + ":parity=tff",
        FieldOrder.BottomFirst => DeinterlaceFilter + ":parity=bff",
        _ => DeinterlaceFilter,
    };

    /// <summary>
    /// DSD (DSD files, SACDs) plays as PCM at this rate. FFmpeg's DSD decoder puts out 1/8 of the
    /// DSD rate (352.8 kHz for DSD64), more than most DACs take.
    /// </summary>
    public const int DsdSampleRate = 192_000;

    /// <summary>The rate to resample an audio codec to; null keeps the source's own rate.</summary>
    public static int? SampleRateFor(string? codec) =>
        codec is not null && (codec.StartsWith("dsd", StringComparison.Ordinal) || codec == "dst") ? DsdSampleRate : null;

    public static IEnumerable<KeyValuePair<string, string>> Audio(PlayerSettings s, int? sourceChannels = null,
        StereoChannel channel = StereoChannel.Both)
    {
        // Downmix only when the user asks; otherwise output the source channel layout.
        yield return P("audio-channels", AudioChannels(s, sourceChannels));
        // Passthrough hands Dolby/DTS bitstreams to the receiver untouched. On Windows that
        // needs WASAPI exclusive mode, which also bypasses the system mixer; the exclusive
        // switch uses it for PCM too, so the device runs at the audio's own rate.
        yield return P("audio-spdif", s.AudioPassthrough ? "ac3,eac3,dts,dts-hd,truehd" : "");
        if (OperatingSystem.IsWindows()) yield return P("audio-exclusive", AudioExclusive(s) ? "yes" : "no");
        // Only the user's normalization switch changes loudness: no downmix normalisation, no
        // ReplayGain.
        yield return P("audio-normalize-downmix", "no");
        yield return P("replaygain", "no");
        yield return P("af", AudioFilters(s, sourceChannels, channel));
        yield return P("volume-max", "150");
        yield return P("volume", F(VolumeCurve.ToMpv(s.Volume)));
        yield return P("mute", s.Muted ? "yes" : "no");
        yield return P("audio-pitch-correction", "yes");
        if (!string.IsNullOrWhiteSpace(s.PreferredAudioLanguages)) yield return P("alang", s.PreferredAudioLanguages);
    }

    /// <summary>mpv's base subtitle font size (at 720p); the size slider scales it.</summary>
    public const int SubtitleFontSize = 44;

    public static IEnumerable<KeyValuePair<string, string>> Subtitles(PlayerSettings s)
    {
        yield return P("sub-auto", s.AutoLoadExternalSubtitles ? "fuzzy" : "no");
        yield return P("sub-scale", F(s.SubtitleScale));
        yield return P("sub-font-size", SubtitleFontSize.ToString(CultureInfo.InvariantCulture));
        yield return P("sub-border-size", "2.5");
        yield return P("sub-ass-override", "no");
        yield return P("embeddedfonts", "yes");
        if (!string.IsNullOrWhiteSpace(s.PreferredSubtitleLanguages)) yield return P("slang", s.PreferredSubtitleLanguages);
    }

    public static IEnumerable<KeyValuePair<string, string>> Screenshots(PortablePaths paths)
    {
        yield return P("screenshot-format", "png");
        yield return P("screenshot-png-compression", "0");
        yield return P("screenshot-png-filter", "0");
        yield return P("screenshot-high-bit-depth", "yes");
        yield return P("screenshot-tag-colorspace", "yes");
        yield return P("screenshot-directory", paths.Screenshots);
        yield return P("screenshot-template", "%F_%wH-%wM-%wS-%wT");
    }

    /// <summary>
    /// Per-file options for anything streamed (torrent, MEGA, URLs): rather than stuttering
    /// through a thin buffer, wait for a few seconds of video before starting or resuming,
    /// and read further ahead. 4K HDR remuxes need both.
    /// </summary>
    public const double InitialStreamBufferSeconds = 5;
    public const double MaxStreamBufferSeconds = 60;

    /// <summary>
    /// After each stall the stream waits for twice as much buffer before resuming (5, 10,
    /// 20, 40, 60 s): a connection slower than the video then gives a few long pauses
    /// instead of constant stutter.
    /// </summary>
    public static double StreamBufferAfterStalls(int stalls) =>
        Math.Min(InitialStreamBufferSeconds * Math.Pow(2, Math.Max(0, stalls)), MaxStreamBufferSeconds);

    public const string StreamingFileOptions =
        "cache-pause-initial=yes,cache-pause-wait=5,demuxer-readahead-secs=90,demuxer-max-bytes=1024MiB";

    public static IEnumerable<KeyValuePair<string, string>> Network()
    {
        // Generous buffering for torrent/HTTP streams.
        yield return P("cache", "yes");
        yield return P("demuxer-max-bytes", "512MiB");
        yield return P("demuxer-max-back-bytes", "128MiB");
        yield return P("demuxer-readahead-secs", "30");
        yield return P("network-timeout", "120");
        yield return P("stream-buffer-size", "4MiB");
    }
}
