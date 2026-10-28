using System.Globalization;
using Paramaja.Core.Mpv;

namespace Paramaja.Core.Playback;

public enum FieldOrder { TopFirst, BottomFirst }

/// <summary>
/// Decides from FFmpeg's idet counts whether a video is interlaced. idet compares the two fields
/// of each frame; on progressive video with fast motion or fine horizontal detail it misjudges
/// a fair share of frames, so it takes a clear majority (most real interlaced video scores 300
/// to 0) before the deinterlacer is switched on.
/// </summary>
public static class InterlaceVerdict
{
    /// <summary>Frames that must look interlaced, at least.</summary>
    public const long MinInterlacedFrames = 60;
    /// <summary>And this many times more than look progressive.</summary>
    public const long Majority = 8;

    /// <summary>The field order if the picture is interlaced, null if it isn't (or can't be told).</summary>
    public static FieldOrder? FromIdet(long tff, long bff, long progressive)
    {
        var interlaced = tff + bff;
        if (interlaced < MinInterlacedFrames || interlaced < Majority * progressive) return null;
        return bff > tff ? FieldOrder.BottomFirst : FieldOrder.TopFirst;
    }

    /// <summary>
    /// Videos worth checking: only standard and high definition frame sizes carry two fields
    /// (352×240 is a single field), and interlaced video has at most 30 frames a second
    /// (60 fields); 50p and 60p video is progressive by nature.
    /// </summary>
    public static bool Applies(int? height, double? fps) =>
        height is >= 400 and <= 1088 && fps is > 0 and <= 31;
}

/// <summary>
/// The picture check for videos whose frames carry no interlace flags (lossless and MJPEG tape
/// captures, some rips): a second, headless mpv decodes a few hundred frames from a little way
/// into the file through FFmpeg's idet and counts how many look interlaced. It runs beside
/// playback, so the playing video's own filter chain is never touched for it.
/// </summary>
public static class InterlaceProbe
{
    /// <summary>Seconds of video decoded; at 25 fps that's 300 frames, a second or so of CPU time.</summary>
    public const double SampleSeconds = 12;

    /// <summary>Where the sample starts: a little way in, past black frames and titles.</summary>
    public static double StartFor(double? duration) =>
        duration is > 0 and var d ? Math.Round(Math.Min(d * 0.1, 60), 3) : 0;

    public static Task<FieldOrder?> AnalyzeAsync(string path, double startSeconds, CancellationToken ct) =>
        Task.Run(() => Analyze(path, startSeconds, ct), ct);

    /// <returns>The field order if the picture is interlaced; null if it isn't, or couldn't be read.</returns>
    public static FieldOrder? Analyze(string path, double startSeconds, CancellationToken ct)
    {
        var (tff, bff, progressive) = Count(path, startSeconds, ct);
        return InterlaceVerdict.FromIdet(tff, bff, progressive);
    }

    internal static (long Tff, long Bff, long Progressive) Count(string path, double startSeconds, CancellationToken ct)
    {
        using var mpv = new MpvClient();
        foreach (var (k, v) in new[]
                 {
                     ("config", "no"), ("load-scripts", "no"), ("ytdl", "no"), ("terminal", "no"), ("osc", "no"),
                     ("input-default-bindings", "no"), ("vo", "null"), ("ao", "null"), ("aid", "no"), ("sid", "no"),
                     ("hwdec", "no"), ("untimed", "yes"), ("keep-open", "yes"), ("idle", "yes"),
                     ("start", startSeconds.ToString("0.###", CultureInfo.InvariantCulture)),
                     ("length", SampleSeconds.ToString(CultureInfo.InvariantCulture)),
                     ("vf", $"@{Label}:lavfi=[idet]"),
                 })
            mpv.SetOption(k, v);
        var failed = false;
        mpv.EndFile += (_, _) => failed = true; // with keep-open only an error (or a stop) ends the file
        mpv.Initialize("no");
        mpv.Command("loadfile", path, "replace");

        // keep-open holds the file (and its filters) at the end, where the counts are read.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        (long, long, long) counts = (0, 0, 0);
        while (!ct.IsCancellationRequested && !failed && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
            if (Read(mpv, "progressive") is { } p) counts = (Read(mpv, "tff") ?? 0, Read(mpv, "bff") ?? 0, p);
            if (mpv.GetFlag("eof-reached") == true) break;
        }
        return counts;
    }

    private const string Label = "paramaja-idet";

    private static long? Read(MpvClient mpv, string kind) =>
        mpv.GetString($"vf-metadata/{Label}/lavfi.idet.multiple.{kind}") is { } s
        && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)v : null;
}
