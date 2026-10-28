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
    /// <summary>Slider position, 0-150. Logarithmic: see <see cref="Playback.VolumeCurve"/>.</summary>
    public double Volume { get; set; } = 100;
    public bool Muted { get; set; }
    public string PreferredAudioLanguages { get; set; } = "";

    // Subtitles
    public bool AutoLoadExternalSubtitles { get; set; } = true;
    public string PreferredSubtitleLanguages { get; set; } = "";
    public double SubtitleScale { get; set; } = 1.0;

    // Playback
    public int SeekSeconds { get; set; } = 5;
    public int LongSeekSeconds { get; set; } = 60;
    public bool ResumePlayback { get; set; }

    // Torrents
    public bool ClearTorrentCacheOnExit { get; set; } = true;
    public int TorrentMetadataTimeoutSeconds { get; set; } = 90;
    public int TorrentListenPort { get; set; }
    public bool TorrentUpnp { get; set; } = true;

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
        GamutMapping = string.IsNullOrWhiteSpace(GamutMapping) ? "perceptual" : GamutMapping;
    }
}
