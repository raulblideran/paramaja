using System.Globalization;
using System.Text;
using Paramaja.Core.Playback;
using Paramaja.Core.Settings;

namespace Paramaja.App.Services;

/// <param name="State">The playing video's state; null when nothing plays.</param>
/// <param name="Samples">Every second since the video started (PlaybackRecorder).</param>
/// <param name="Details">The engine's own account of how it plays the video (IPlayer.Details).</param>
public sealed record PlaybackLogInput(
    PlayerState? State,
    PlayerSettings Settings,
    RenderProfile? Profile,
    PlaybackHealth Health,
    IReadOnlyList<HealthSample> Samples,
    MediaSource Source,
    string TorrentStatus,
    string Engine,
    IReadOnlyList<KeyValuePair<string, string>> Details,
    IReadOnlyList<string> Messages,
    DateTime Now,
    IReadOnlyList<GpuAdapter>? Adapters = null,
    IReadOnlyList<string>? Displays = null,
    string? Power = null);

/// <summary>
/// The log Create Log saves: the machine, the video and how it's played, every second's playback
/// statistics since it started, a summary and the engine's latest messages. Plain text, for
/// benchmarking and for whoever is helping ("it lags on my laptop" needs exactly this).
/// </summary>
public static class PlaybackLog
{
    public static string FileName(DateTime now) => $"paramaja-{now:yyyyMMdd-HHmmss}.log";

    /// <summary>Writes the log into <paramref name="folder"/> and returns its path.</summary>
    public static string Write(string folder, string text, DateTime now)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName(now));
        File.WriteAllText(path, text);
        return path;
    }

    private static string F(double? v, string format = "0.0") =>
        v is { } d && double.IsFinite(d) ? d.ToString(format, CultureInfo.InvariantCulture) : "-";

    public static string Build(PlaybackLogInput input)
    {
        var sb = new StringBuilder();
        var s = input.Settings;
        sb.AppendLine($"{AppInfo.Name} playback log, {input.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine();

        if (input.State is { } state)
        {
            sb.AppendLine(DiagnosticsReport.Build(state, s, input.Profile, input.Health, input.Source, input.TorrentStatus));
            sb.AppendLine();
            sb.AppendLine($"Title: {state.Title}");
        }
        else
        {
            sb.AppendLine($"{AppInfo.Name} {AppInfo.Display}");
            sb.AppendLine($"OS: {SystemInfo.Os}");
            sb.AppendLine($"CPU: {SystemInfo.Cpu}");
            sb.AppendLine($"GPU: {string.Join("; ", SystemInfo.Gpus)}");
            sb.AppendLine($"RAM: {SystemInfo.Memory}");
            sb.AppendLine();
            sb.AppendLine("Nothing is playing: play the video, then create the log while it plays (or right after).");
        }
        sb.AppendLine($"Engine: {input.Engine}");
        foreach (var (key, value) in input.Details) sb.AppendLine($"{key}: {value}");
        var adapters = input.Adapters ?? GpuAdapters.List();
        if (OperatingSystem.IsWindows() || adapters.Count > 0)
            sb.AppendLine($"Graphics adapters: {GpuAdapters.Describe(adapters)}; high-performance GPU preferred: {(s.PreferHighPerformanceGpu ? "yes" : "no")}");
        foreach (var d in input.Displays ?? SystemInfo.Displays) sb.AppendLine($"Display: {d}");
        sb.AppendLine($"Power: {input.Power ?? SystemInfo.Power}");
        sb.AppendLine();

        sb.AppendLine("Settings");
        sb.AppendLine($"  Rendering: {s.RenderQuality} (using {input.Profile?.ToString() ?? "?"}); frame timing: " +
                      (s.SyncToDisplay ? $"synced to the display at {(s.DisplayFps == 0 ? "its own rate" : $"{s.DisplayFps:0.###} Hz")}" : "audio clock"));
        sb.AppendLine($"  HDR: {s.HdrMode}, curve {s.ToneMapping}, scene-adaptive {(s.HdrDynamicPeak ? "on" : "off")}, highlight recovery {s.HdrContrastRecovery:0.00}");
        sb.AppendLine($"  Audio: downmix to stereo {(s.DownmixToStereo ? "on" : "off")}, normalize {(s.NormalizeAudio ? "on" : "off")}, passthrough {(s.AudioPassthrough ? "on" : "off")}");
        sb.AppendLine();

        AppendTable(sb, input.Samples);
        AppendSummary(sb, input.Samples, input.Health, input.State);

        sb.AppendLine($"Engine messages (last {input.Messages.Count})");
        sb.AppendLine(new string('-', 60));
        foreach (var m in input.Messages) sb.AppendLine(m);
        return sb.ToString();
    }

    /// <summary>A counter's increase since the previous second (counters restart with each video).</summary>
    private static long Delta(long? now, long? before) => now is { } n && before is { } b && n >= b ? n - b : 0;

    private static void AppendTable(StringBuilder sb, IReadOnlyList<HealthSample> samples)
    {
        sb.AppendLine("Every second (dropped, decoder, late and mistimed are frames in that second)");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine("time      position  state      out fps  disp Hz  meas Hz  dropped  decoder  late  mistimed  jitter  avsync ms  gpu ms  cache s  in MB/s  cpu %  mem MB");
        for (var i = 0; i < samples.Count; i++)
        {
            var x = samples[i];
            var prev = i > 0 ? samples[i - 1] : null;
            var state = x.Buffering ? "buffering" : x.Playing ? "playing" : "paused";
            sb.Append(CultureInfo.InvariantCulture, $"{x.At.ToLocalTime():HH:mm:ss}  {TimeFormat.Format(x.Position),8}  {state,-9}");
            sb.Append(CultureInfo.InvariantCulture, $"  {F(x.OutputFps),7}  {F(x.DisplayFps),7}  {F(x.MeasuredDisplayFps),7}");
            sb.Append(CultureInfo.InvariantCulture, $"  {Delta(x.FrameDrops, prev?.FrameDrops),7}  {Delta(x.DecoderDrops, prev?.DecoderDrops),7}");
            sb.Append(CultureInfo.InvariantCulture, $"  {Delta(x.DelayedFrames, prev?.DelayedFrames),4}  {Delta(x.MistimedFrames, prev?.MistimedFrames),8}");
            sb.Append(CultureInfo.InvariantCulture, $"  {F(x.VsyncJitter, "0.000"),6}  {F(x.AvSync * 1000),9}  {F(x.GpuMicroseconds / 1000, "0.00"),6}");
            sb.Append(CultureInfo.InvariantCulture, $"  {F(x.CachedSeconds),7}  {F(x.InputBytesPerSecond / 1_000_000, "0.00"),7}  {F(x.CpuPercent),5}  {F(x.MemoryMb, "0"),6}");
            sb.AppendLine();
        }
        if (samples.Count == 0) sb.AppendLine("(nothing recorded yet: statistics are recorded while a video plays)");
        sb.AppendLine();
    }

    private static void AppendSummary(StringBuilder sb, IReadOnlyList<HealthSample> samples, PlaybackHealth health, PlayerState? state)
    {
        sb.AppendLine("Summary");
        sb.AppendLine(new string('-', 60));
        var playing = samples.Where(x => x.Playing).ToList();
        sb.AppendLine($"Recorded: {samples.Count} s, {playing.Count} s of them playing");
        if (samples.Count > 0)
        {
            var first = samples[0];
            var last = samples[^1];
            sb.AppendLine($"Output: {F(Average(playing, x => x.OutputFps))} fps on average (source {F(state?.ContainerFps, "0.###")} fps); " +
                          $"display: mpv timed for {F(Average(samples, x => x.DisplayFps))} Hz, measured {F(Average(samples, x => x.MeasuredDisplayFps))} Hz");
            sb.AppendLine($"Frames: dropped {Delta(last.FrameDrops, first.FrameDrops)}, dropped by the decoder {Delta(last.DecoderDrops, first.DecoderDrops)}, " +
                          $"late {Delta(last.DelayedFrames, first.DelayedFrames)}, mistimed {Delta(last.MistimedFrames, first.MistimedFrames)}");
            var gpu = samples.Select(x => x.GpuMicroseconds).OfType<double>().ToList();
            if (gpu.Count > 0) sb.AppendLine($"GPU time per frame: {F(gpu.Average() / 1000, "0.00")} ms on average, {F(gpu.Max() / 1000, "0.00")} ms at worst");
            var cpu = samples.Select(x => x.CpuPercent).OfType<double>().ToList();
            if (cpu.Count > 0) sb.AppendLine($"CPU: {F(cpu.Average())}% on average, {F(cpu.Max())}% at worst (100% is one core)");
            sb.AppendLine($"Buffering stalls: {health.Stalls}");

            var worst = samples.Skip(1).Select((x, i) => (x, Lost: Delta(x.FrameDrops, samples[i].FrameDrops) + Delta(x.DecoderDrops, samples[i].DecoderDrops)
                                                                     + Delta(x.DelayedFrames, samples[i].DelayedFrames)))
                .Where(w => w.Lost > 0).OrderByDescending(w => w.Lost).Take(5).ToList();
            sb.AppendLine(worst.Count == 0 ? "Worst seconds: none, no frame was dropped or late"
                : "Worst seconds: " + string.Join(", ", worst.Select(w => $"{TimeFormat.Format(w.x.Position)} ({w.Lost} frames)")));
        }
        sb.AppendLine();
    }

    private static double? Average(IEnumerable<HealthSample> samples, Func<HealthSample, double?> value)
    {
        var values = samples.Select(value).OfType<double>().Where(double.IsFinite).ToList();
        return values.Count == 0 ? null : values.Average();
    }
}
