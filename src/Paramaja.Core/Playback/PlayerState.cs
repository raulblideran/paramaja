namespace Paramaja.Core.Playback;

/// <summary>Latest values of the mpv properties the UI cares about.</summary>
public sealed class PlayerState
{
    public double Position { get; set; }
    public double Duration { get; set; }
    public double Volume { get; set; } = 100;
    public bool IsPaused { get; set; }
    public bool IsMuted { get; set; }
    public bool IsBuffering { get; set; }
    public bool IsSeeking { get; set; }
    public bool DeinterlaceActive { get; set; }
    /// <summary>"BWDIF" while deinterlacing, otherwise null.</summary>
    public string? DeinterlaceFilter { get; set; }
    /// <summary>The current file's frames are flagged interlaced.</summary>
    public bool SourceInterlaced { get; set; }
    public bool EofReached { get; set; }
    public bool IsIdle { get; set; } = true;
    public double? BufferingPercent { get; set; }
    public double? CacheTime { get; set; }
    public double? ContainerFps { get; set; }
    public double? OutputFps { get; set; }
    public int? SourceChannels { get; set; }
    public int? OutputChannels { get; set; }
    public int? VideoWidth { get; set; }
    public int? VideoHeight { get; set; }
    public string? Title { get; set; }
    public string? Path { get; set; }
    public string? HwdecCurrent { get; set; }
    public string? VideoCodec { get; set; }
    public string? VideoFormat { get; set; }
    public string? Transfer { get; set; }
    public string? Primaries { get; set; }
    public string? Deinterlace { get; set; }
    public string? VideoFilters { get; set; }
    public string? CurrentVo { get; set; }

    /// <summary>PQ (HDR10/DV) or HLG transfer.</summary>
    public bool IsHdr => Transfer is "pq" or "hlg";

    public bool IsHardwareDecoding => !string.IsNullOrEmpty(HwdecCurrent) && HwdecCurrent != "no";
}
