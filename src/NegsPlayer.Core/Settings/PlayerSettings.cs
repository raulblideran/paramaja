using System.Text.Json.Serialization;

namespace NegsPlayer.Core.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<HdrMode>))]
public enum HdrMode
{
    /// <summary>Tonemap HDR to SDR on the fly (like PotPlayer / madVR).</summary>
    TonemapToSdr,
    /// <summary>Send HDR untouched to an HDR-capable display.</summary>
    Passthrough,
}

[JsonConverter(typeof(JsonStringEnumConverter<DeinterlaceMode>))]
public enum DeinterlaceMode
{
    /// <summary>Deinterlace only content flagged as interlaced.</summary>
    Auto,
    On,
    Off,
}

public sealed class PlayerSettings
{
    public static readonly string[] ToneMappingCurves =
        ["spline", "bt.2446a", "bt.2390", "st2094-40", "hable", "mobius", "reinhard", "clip", "auto"];

    // Video
    public bool HardwareDecoding { get; set; } = true;
    public HdrMode HdrMode { get; set; } = HdrMode.TonemapToSdr;
    public string ToneMapping { get; set; } = "spline";
    /// <summary>Measure each frame's brightness so tonemapping adapts scene by scene.</summary>
    public bool HdrDynamicPeak { get; set; } = true;
    /// <summary>Restores fine detail lost in highlights during tonemapping (gpu-next).</summary>
    public double HdrContrastRecovery { get; set; } = 0.3;
    public string GamutMapping { get; set; } = "perceptual";
    public DeinterlaceMode Deinterlace { get; set; } = DeinterlaceMode.Auto;

    // Audio — never normalised; downmix is strictly opt-in.
    public bool DownmixToStereo { get; set; }
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

    public List<string> RecentItems { get; set; } = [];
    public const int MaxRecentItems = 15;

    public void AddRecent(string item)
    {
        RecentItems.RemoveAll(r => string.Equals(r, item, StringComparison.Ordinal));
        RecentItems.Insert(0, item);
        if (RecentItems.Count > MaxRecentItems) RecentItems.RemoveRange(MaxRecentItems, RecentItems.Count - MaxRecentItems);
    }

    /// <summary>Clamps values that may have been hand-edited into nonsense.</summary>
    public void Normalize()
    {
        if (!ToneMappingCurves.Contains(ToneMapping)) ToneMapping = "spline";
        HdrContrastRecovery = Math.Clamp(HdrContrastRecovery, 0, 2);
        Volume = Math.Clamp(Volume, 0, 150);
        SubtitleScale = Math.Clamp(SubtitleScale, 0.25, 4);
        SeekSeconds = Math.Clamp(SeekSeconds, 1, 600);
        LongSeekSeconds = Math.Clamp(LongSeekSeconds, 1, 3600);
        TorrentMetadataTimeoutSeconds = Math.Clamp(TorrentMetadataTimeoutSeconds, 10, 600);
        TorrentListenPort = Math.Clamp(TorrentListenPort, 0, 65535);
        RecentItems ??= [];
        PreferredAudioLanguages ??= "";
        PreferredSubtitleLanguages ??= "";
        GamutMapping = string.IsNullOrWhiteSpace(GamutMapping) ? "perceptual" : GamutMapping;
    }
}
