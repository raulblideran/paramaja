using System.Text;
using Paramaja.Core.Playback;
using Paramaja.Core.Settings;

namespace Paramaja.App.Services;

public enum MediaSource { None, Local, Torrent, Mega, Web }

/// <summary>
/// A plain-text report a friend can paste: the build, the machine, what's playing and how,
/// and a verdict on why it's struggling. File names are left out on purpose.
/// </summary>
public static class DiagnosticsReport
{
    public static string Build(PlayerState state, PlayerSettings settings, RenderProfile? profile,
        PlaybackHealth health, MediaSource source, string torrentStatus,
        string? os = null, string? cpu = null, IReadOnlyList<string>? gpus = null, string? memory = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{AppInfo.Name} {AppInfo.Display}");
        sb.AppendLine($"OS: {os ?? SystemInfo.Os}");
        sb.AppendLine($"CPU: {cpu ?? SystemInfo.Cpu}");
        var g = gpus ?? SystemInfo.Gpus;
        sb.AppendLine($"GPU: {(g.Count == 0 ? "unknown" : string.Join("; ", g))}");
        sb.AppendLine($"RAM: {memory ?? SystemInfo.Memory}");
        sb.AppendLine();

        sb.AppendLine($"Source: {source}");
        var codec = CodecLabels.Describe(state.VideoFormat);
        sb.AppendLine($"Video: {(codec.Length > 0 ? codec : "?")} {state.VideoWidth}x{state.VideoHeight}, " +
                      $"{(state.IsHdr ? $"HDR ({state.Transfer})" : "SDR")}, {state.ContainerFps:0.###} fps, " +
                      (state.SourceInterlaced ? $"interlaced ({(state.InterlaceDetectedBy == PlayerState.ByPicture ? "seen in the picture" : "flagged")})" : "progressive"));
        sb.AppendLine($"Decoding: {(state.IsHardwareDecoding ? $"hardware ({state.HwdecCurrent})" : "software (CPU)")}" +
                      (!state.HardwareDecodingApplies ? ", hardware decoding doesn't apply to this video"
                          : state.HardwareDecodingOn ? "" : ", hardware decoding switched off for this video"));
        sb.AppendLine($"Rendering: {profile?.ToString() ?? "?"} (setting: {settings.RenderQuality}), output {state.OutputFps:0.##} fps, VO {state.CurrentVo}");
        sb.AppendLine($"HDR: {settings.HdrMode}, curve {settings.ToneMapping}; deinterlace {state.DeinterlaceFilter ?? "off"}; " +
                      $"frame timing {(settings.SyncToDisplay ? "display" : "audio clock")}");
        sb.AppendLine($"Audio: {state.SourceChannels}ch → {state.OutputChannels}ch, passthrough {(settings.AudioPassthrough ? "on" : "off")}");
        sb.AppendLine();

        var s = health.Latest;
        sb.AppendLine($"Dropped frames/s (last {PlaybackHealth.Window.TotalSeconds:0} s): rendering {health.RenderDropsPerSecond:0.0}, decoder {health.DecoderDropsPerSecond:0.0}");
        sb.AppendLine($"Totals: dropped {s?.FrameDrops}, decoder dropped {s?.DecoderDrops}, late {s?.DelayedFrames}");
        sb.AppendLine($"Buffering stalls: {health.Stalls}, buffered ahead {s?.CachedSeconds:0.#} s");
        sb.AppendLine($"Download {Mb(health.InputBytesPerSecond)}, video needs {Mb(health.RequiredBytesPerSecond)}");
        if (!string.IsNullOrEmpty(torrentStatus)) sb.AppendLine($"Torrent: {torrentStatus}");
        sb.AppendLine();

        var findings = health.Findings(state.HwdecCurrent, state.VideoFormat, state.VideoHeight);
        sb.AppendLine("Verdict:");
        if (findings.Count == 0) sb.AppendLine("- No problems detected in the last few seconds.");
        foreach (var f in findings) sb.AppendLine($"- {health.Explain(f)}");
        return sb.ToString().TrimEnd();
    }

    private static string Mb(double? bytesPerSecond) =>
        bytesPerSecond is { } b ? $"{b / 1_000_000:0.0} MB/s" : "n/a";
}
