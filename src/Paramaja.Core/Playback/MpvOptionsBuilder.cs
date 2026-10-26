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
        o.AddRange(Ytdl(ytdlp ?? new YtDlp(AppContext.BaseDirectory)));
        o.AddRange(Video(s));
        o.AddRange(Hdr(s));
        o.AddRange(Deinterlace(s));
        o.AddRange(Audio(s));
        o.AddRange(Subtitles(s));
        o.AddRange(Screenshots(paths));
        o.AddRange(Network());
        return o;
    }

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
        yield return P("cursor-autohide", "1500");
        // Precise seeks are required for exact frame stepping.
        yield return P("hr-seek", "yes");
        yield return P("hr-seek-framedrop", "no");
        yield return P("terminal", "no");
        // Drops are handled by the app (magnet text, subtitles), not by mpv's video window.
        yield return P("drag-and-drop", "no");
    }

    /// <summary>YouTube and other sites: mpv's ytdl hook hands page URLs to yt-dlp.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Ytdl(YtDlp ytdlp)
    {
        yield return P("ytdl", "yes");
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
        yield return P("hwdec", s.HardwareDecoding ? "auto-safe" : "no");
        yield return P("video-sync", "display-resample");
        yield return P("interpolation", "no");
        yield return P("scale", "ewa_lanczossharp");
        yield return P("cscale", "ewa_lanczossharp");
        yield return P("dscale", "mitchell");
        yield return P("deband", "no");
    }

    public static IEnumerable<KeyValuePair<string, string>> Hdr(PlayerSettings s)
    {
        if (s.HdrMode == HdrMode.Passthrough)
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
        yield return P("hdr-compute-peak", s.HdrDynamicPeak ? "yes" : "no");
        yield return P("hdr-peak-percentile", "99.995");
        yield return P("hdr-contrast-recovery", F(s.HdrContrastRecovery));
        yield return P("gamut-mapping-mode", s.GamutMapping);
    }

    public static IEnumerable<KeyValuePair<string, string>> Deinterlace(PlayerSettings s)
    {
        // mpv's own deinterlace option would pick the GPU's deinterlacer when hardware
        // decoding, whatever we asked for. PlayerService inserts the chosen filter itself.
        yield return P("deinterlace", "no");
    }

    public const string DeinterlaceLabel = "@paramaja-deint";

    /// <summary>The video filter for an algorithm. Both output one frame per field (50i → 50p).</summary>
    public static string DeinterlaceFilter(DeinterlaceAlgorithm algorithm) => algorithm switch
    {
        DeinterlaceAlgorithm.Bob => $"{DeinterlaceLabel}:lavfi=[separatefields,scale=w=iw:h=ih*2:flags=bicubic]",
        _ => $"{DeinterlaceLabel}:bwdif=mode=send_field",
    };

    public static IEnumerable<KeyValuePair<string, string>> Audio(PlayerSettings s)
    {
        // Downmix only when the user asks; otherwise output the source channel layout.
        yield return P("audio-channels", s.DownmixToStereo && !s.AudioPassthrough ? "stereo" : "auto");
        // Passthrough hands Dolby/DTS bitstreams to the receiver untouched. On Windows that
        // needs WASAPI exclusive mode, which also bypasses the system mixer.
        yield return P("audio-spdif", s.AudioPassthrough ? "ac3,eac3,dts,dts-hd,truehd" : "");
        if (OperatingSystem.IsWindows()) yield return P("audio-exclusive", s.AudioPassthrough ? "yes" : "no");
        // Never normalise: no downmix normalisation, no ReplayGain, no loudness filters.
        yield return P("audio-normalize-downmix", "no");
        yield return P("replaygain", "no");
        yield return P("af", "");
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
