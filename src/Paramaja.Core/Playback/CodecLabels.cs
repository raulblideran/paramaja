namespace Paramaja.Core.Playback;

/// <summary>Short, familiar names for video codecs, for the codec badge.</summary>
public static class CodecLabels
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["h264"] = "H.264",
        ["hevc"] = "HEVC",
        ["av1"] = "AV1",
        ["vp9"] = "VP9",
        ["vp8"] = "VP8",
        ["vp6f"] = "VP6",
        ["vp6"] = "VP6",
        ["vp6a"] = "VP6",
        ["mpeg2video"] = "MPEG-2",
        ["mpeg1video"] = "MPEG-1",
        ["mpeg4"] = "MPEG-4",
        ["msmpeg4v3"] = "DivX 3",
        ["msmpeg4v2"] = "MPEG-4 v2",
        ["h263"] = "H.263",
        ["flv1"] = "Sorenson",
        ["vc1"] = "VC-1",
        ["wmv3"] = "WMV 9",
        ["wmv2"] = "WMV 8",
        ["wmv1"] = "WMV 7",
        ["theora"] = "Theora",
        ["prores"] = "ProRes",
        ["dnxhd"] = "DNxHD",
        ["cfhd"] = "CineForm",
        ["ffv1"] = "FFV1",
        ["huffyuv"] = "HuffYUV",
        ["ffvhuff"] = "HuffYUV",
        ["lagarith"] = "Lagarith",
        ["utvideo"] = "Ut Video",
        ["rawvideo"] = "Uncompressed",
        ["v210"] = "Uncompressed 10-bit",
        ["dvvideo"] = "DV",
        ["mjpeg"] = "MJPEG",
        ["rv40"] = "RealVideo 4",
        ["rv30"] = "RealVideo 3",
        ["cinepak"] = "Cinepak",
        ["indeo5"] = "Indeo 5",
    };

    /// <summary>"H.264" for FFmpeg's "h264", and so on; unknown codecs in capitals; "" for none.</summary>
    public static string Describe(string? codec) =>
        string.IsNullOrWhiteSpace(codec) ? ""
        : Names.TryGetValue(codec.Trim(), out var name) ? name
        : codec.Trim().ToUpperInvariant();

    /// <summary>
    /// libVLC names codecs by FourCC ("mpgv", "h264"); this gives FFmpeg's name for them, which is
    /// what the rest of the app (and <see cref="Describe"/>) speaks.
    /// </summary>
    public static string? FromFourCC(string? fourcc) => fourcc?.Trim() switch
    {
        null or "" => null,
        "mpgv" or "mp2v" => "mpeg2video",
        "mp1v" => "mpeg1video",
        "h264" or "avc1" or "H264" => "h264",
        "hevc" or "h265" or "HEVC" => "hevc",
        "WVC1" or "wvc1" or "VC-1" or "vc-1" => "vc1",
        "mp4v" or "DIVX" or "XVID" => "mpeg4",
        "av01" => "av1",
        "VP90" or "vp90" => "vp9",
        var other => other.ToLowerInvariant(),
    };
}
