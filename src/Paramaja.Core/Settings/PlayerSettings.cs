using System.Text.Json.Serialization;

namespace Paramaja.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<HdrMode>))]
public enum HdrMode
{
    /// <summary>Passthrough when the display is in HDR mode, otherwise tonemap.</summary>
    Auto,
    /// <summary>Tonemap HDR to SDR on the fly (like PotPlayer / madVR).</summary>
    TonemapToSdr,
    /// <summary>Send HDR untouched to an HDR-capable display.</summary>
    Passthrough,
}

/// <summary>The rendering pipeline actually in use.</summary>
public enum RenderProfile
{
    /// <summary>mpv's best scalers (EWA) and full HDR analysis.</summary>
    High,
    /// <summary>Separable scalers and no contrast recovery: most of the quality at a fraction of the cost.</summary>
    Balanced,
    /// <summary>Bilinear and static HDR tonemapping: about what VLC's renderer costs.</summary>
    Fast,
}

[JsonConverter(typeof(JsonStringEnumConverter<RenderQuality>))]
public enum RenderQuality
{
    /// <summary>High below 4K, Balanced for 4K, and Fast if frames still drop.</summary>
    Auto,
    High,
    Balanced,
    Fast,
}

public sealed class PlayerSettings
{
    /// <summary>Bumped when a default changes for existing users too (see <see cref="Normalize"/>).</summary>
    public const int CurrentVersion = 4;

    /// <summary>The settings.json format; 0 for files written before versioning.</summary>
    public int SettingsVersion { get; set; }

    public static readonly string[] ToneMappingCurves =
        ["mobius", "spline", "bt.2446a", "bt.2390", "st2094-40", "hable", "reinhard", "clip", "auto"];

    // Video. Hardware decoding and deinterlacing aren't settings: they're decided for each video
    // (PlayerService), and their switches in the video menu last for that video only.
    /// <summary>
    /// Times frames to the display's refresh (mpv's display-resample) instead of the audio clock.
    /// Off by default: with a refresh rate mpv gets wrong (high-refresh monitors, laptops with two
    /// GPUs) it made playback stutter, and the audio clock never does.
    /// </summary>
    public bool SyncToDisplay { get; set; }
    /// <summary>
    /// The display refresh rate <see cref="SyncToDisplay"/> times frames for, in Hz; 0 (default)
    /// trusts what the monitor reports.
    /// </summary>
    public double DisplayFps { get; set; }
    /// <summary>
    /// Windows laptops with two GPUs: render and decode on the high-performance one, as games do,
    /// rather than the power-saving one Windows hands out by default. Takes effect at the next start.
    /// </summary>
    public bool PreferHighPerformanceGpu { get; set; } = true;
    /// <summary>Highest resolution streamed from YouTube and other sites (e.g. 1080); 0 for the best.</summary>
    public int StreamMaxHeight { get; set; }
    public RenderQuality RenderQuality { get; set; } = RenderQuality.Balanced;
    public HdrMode HdrMode { get; set; } = HdrMode.Auto;
    /// <summary>The HDR → SDR curve; mobius keeps in-range colours exact and rolls off only the highlights.</summary>
    public string ToneMapping { get; set; } = "mobius";
    /// <summary>Measure each frame's brightness so tonemapping adapts scene by scene.</summary>
    public bool HdrDynamicPeak { get; set; } = true;
    /// <summary>Restores fine detail lost in highlights during tonemapping (gpu-next).</summary>
    public double HdrContrastRecovery { get; set; } = 0.3;
    public string GamutMapping { get; set; } = "perceptual";

    // Audio — never normalised; downmix is strictly opt-in.
    /// <summary>Applies to surround sources only (5.1, 7.1…).</summary>
    public bool DownmixToStereo { get; set; }
    /// <summary>Evens out loud and quiet passages (dynamic normalization). Off by default.</summary>
    public bool NormalizeAudio { get; set; }
    /// <summary>Send Dolby/DTS bitstreams untouched to an AV receiver (WASAPI exclusive on Windows).</summary>
    public bool AudioPassthrough { get; set; }
    /// <summary>
    /// Windows: the audio device is the player's alone (WASAPI exclusive), so it runs at each
    /// file's own rate (192 kHz for DSD) instead of the system mixer's. Other apps go quiet.
    /// </summary>
    public bool ExclusiveAudio { get; set; }
    /// <summary>Slider position, 0-150. Logarithmic: see <see cref="Playback.VolumeCurve"/>.</summary>
    public double Volume { get; set; } = 100;
    public bool Muted { get; set; }
    public string PreferredAudioLanguages { get; set; } = "";

    // Subtitles
    public bool AutoLoadExternalSubtitles { get; set; } = true;
    public string PreferredSubtitleLanguages { get; set; } = "";
    public double SubtitleScale { get; set; } = 1.0;
    /// <summary>The text subtitles' font family; empty for the default (sans-serif).</summary>
    public string SubtitleFont { get; set; } = "";
    public bool SubtitleBold { get; set; }
    /// <summary>Colours as #RRGGBB.</summary>
    public string SubtitleColor { get; set; } = DefaultSubtitleColor;
    public string SubtitleBorderColor { get; set; } = DefaultSubtitleBorderColor;
    /// <summary>The outline's thickness (mpv's sub-border-size, at 720p); 0 for none.</summary>
    public double SubtitleBorderSize { get; set; } = DefaultSubtitleBorderSize;
    /// <summary>The drop shadow's offset (mpv's sub-shadow-offset); 0 for none.</summary>
    public double SubtitleShadow { get; set; }
    /// <summary>A box behind the text, as TV subtitles have, instead of the outline.</summary>
    public bool SubtitleBackground { get; set; }
    /// <summary>The box's opacity, 0 to 1 (black).</summary>
    public double SubtitleBackgroundOpacity { get; set; } = 0.6;
    /// <summary>Where the subtitles sit: 100 at the bottom (mpv's sub-pos), less is higher up.</summary>
    public int SubtitlePosition { get; set; } = 100;
    /// <summary>Styled subtitles (ASS/SSA, anime's) take these settings too, instead of their own look.</summary>
    public bool SubtitleStyleAss { get; set; }

    public const string DefaultSubtitleColor = "#FFFFFF";
    public const string DefaultSubtitleBorderColor = "#000000";
    public const double DefaultSubtitleBorderSize = 2.5;

    /// <summary>Puts every subtitle style setting back as it was out of the box.</summary>
    public void ResetSubtitleStyle()
    {
        SubtitleScale = 1.0;
        SubtitleFont = "";
        SubtitleBold = false;
        SubtitleColor = DefaultSubtitleColor;
        SubtitleBorderColor = DefaultSubtitleBorderColor;
        SubtitleBorderSize = DefaultSubtitleBorderSize;
        SubtitleShadow = 0;
        SubtitleBackground = false;
        SubtitleBackgroundOpacity = 0.6;
        SubtitlePosition = 100;
        SubtitleStyleAss = false;
    }

    // The controls bar
    /// <summary>
    /// The controls bar's buttons the user chose to hide (<see cref="ControlIds"/>); empty shows
    /// them all. Play and Settings always show.
    /// </summary>
    public List<string> HiddenControls { get; set; } = [];

    /// <summary>The controls bar's buttons that can be hidden, in the bar's order.</summary>
    public static readonly IReadOnlyList<string> ControlIds =
    [
        "files", "frames", "stop", "chapters", "volume", "badges", "discmenu", "chaptermenu", "subtitles", "audio", "video",
        "screenshot", "download", "openlink", "open", "fullscreen",
    ];

    // Playback
    public int SeekSeconds { get; set; } = 5;
    public int LongSeekSeconds { get; set; } = 60;
    public bool ResumePlayback { get; set; }

    // Torrents
    public bool ClearTorrentCacheOnExit { get; set; } = true;
    public int TorrentMetadataTimeoutSeconds { get; set; } = 90;
    public int TorrentListenPort { get; set; }
    public bool TorrentUpnp { get; set; } = true;

    /// <summary>What belongs to this machine rather than to the person: kept when settings come from another install.</summary>
    public static readonly IReadOnlySet<string> MachineSettings = new HashSet<string>
    {
        nameof(PreferHighPerformanceGpu), nameof(ExclusiveAudio), nameof(DisplayFps), nameof(TorrentListenPort),
    };

    /// <summary>
    /// Takes every setting from <paramref name="other"/> (another install's), except this
    /// machine's own (<see cref="MachineSettings"/>). Copied into this object rather than
    /// replacing it: both engines hold it.
    /// </summary>
    public void CopyFrom(PlayerSettings other)
    {
        foreach (var p in typeof(PlayerSettings).GetProperties())
        {
            if (!p.CanWrite || !p.CanRead || MachineSettings.Contains(p.Name)) continue;
            var value = p.GetValue(other);
            if (value is List<string> list) value = list.ToList();
            p.SetValue(this, value);
        }
        Normalize();
    }

    /// <summary>"#RRGGBB" in capitals for a colour written as #rgb, #rrggbb or rrggbb; else the default.</summary>
    public static string NormalColor(string? value, string fallback)
    {
        var v = (value ?? "").Trim().TrimStart('#');
        if (v.Length == 3) v = string.Concat(v.Select(c => $"{c}{c}"));
        return v.Length == 6 && v.All(Uri.IsHexDigit) ? "#" + v.ToUpperInvariant() : fallback;
    }

    /// <summary>What <see cref="HdrMode"/> amounts to on a display that is (or isn't) in HDR mode.</summary>
    public HdrMode EffectiveHdrMode(bool displayIsHdr) => HdrMode switch
    {
        HdrMode.Auto => displayIsHdr ? HdrMode.Passthrough : HdrMode.TonemapToSdr,
        var m => m,
    };

    /// <summary>Clamps values that may have been hand-edited into nonsense.</summary>
    public void Normalize()
    {
        // 1.5.0 made Balanced the default; whoever was on the old default (Auto) moves too.
        if (SettingsVersion < 2 && RenderQuality == RenderQuality.Auto) RenderQuality = RenderQuality.Balanced;
        // 1.7.0 made mobius the default curve; whoever was on the old default (spline) moves too.
        if (SettingsVersion < 3 && ToneMapping == "spline") ToneMapping = "mobius";
        // 1.8.0 made display sync opt-in, timed for the monitor's own rate; the old default (60) moves too.
        if (SettingsVersion < 4 && DisplayFps == 60) DisplayFps = 0;
        SettingsVersion = CurrentVersion;
        if (!ToneMappingCurves.Contains(ToneMapping)) ToneMapping = "mobius";
        HdrContrastRecovery = Math.Clamp(HdrContrastRecovery, 0, 2);
        Volume = Math.Clamp(Volume, 0, 150);
        if (AudioPassthrough) DownmixToStereo = false;
        SubtitleScale = Math.Clamp(SubtitleScale, 0.25, 4);
        SeekSeconds = Math.Clamp(SeekSeconds, 1, 600);
        DisplayFps = DisplayFps is 0 ? 0 : Math.Clamp(DisplayFps, 23, 500);
        StreamMaxHeight = Math.Clamp(StreamMaxHeight, 0, 4320);
        LongSeekSeconds = Math.Clamp(LongSeekSeconds, 1, 3600);
        TorrentMetadataTimeoutSeconds = Math.Clamp(TorrentMetadataTimeoutSeconds, 10, 600);
        TorrentListenPort = Math.Clamp(TorrentListenPort, 0, 65535);
        PreferredAudioLanguages ??= "";
        PreferredSubtitleLanguages ??= "";
        SubtitleFont = SubtitleFont?.Trim() ?? "";
        SubtitleColor = NormalColor(SubtitleColor, DefaultSubtitleColor);
        SubtitleBorderColor = NormalColor(SubtitleBorderColor, DefaultSubtitleBorderColor);
        SubtitleBorderSize = Math.Clamp(SubtitleBorderSize, 0, 10);
        SubtitleShadow = Math.Clamp(SubtitleShadow, 0, 10);
        SubtitleBackgroundOpacity = Math.Clamp(SubtitleBackgroundOpacity, 0, 1);
        SubtitlePosition = Math.Clamp(SubtitlePosition, 0, 150);
        HiddenControls = (HiddenControls ?? []).Where(ControlIds.Contains).Distinct().ToList();
        GamutMapping = string.IsNullOrWhiteSpace(GamutMapping) ? "perceptual" : GamutMapping;
    }
}
