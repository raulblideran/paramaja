using System.Diagnostics;

namespace Paramaja.Core.Playback;

/// <summary>
/// yt-dlp makes YouTube and ~1800 other sites playable: mpv's built-in ytdl hook calls it to
/// turn a page URL into stream URLs. The portable build ships yt-dlp.exe plus QuickJS
/// (yt-dlp needs a JavaScript runtime for YouTube) next to the app.
/// </summary>
public sealed class YtDlp(string appDirectory)
{
    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public string? BundledPath =>
        File.Exists(Path.Combine(appDirectory, Exe("yt-dlp"))) ? Path.Combine(appDirectory, Exe("yt-dlp")) : null;

    public string? BundledJsRuntime =>
        File.Exists(Path.Combine(appDirectory, Exe("qjs"))) ? Path.Combine(appDirectory, Exe("qjs")) : null;

    /// <summary>Runs <c>yt-dlp -U</c>; sites change often, and this fixes most "can't play" cases.</summary>
    public async Task<string> UpdateAsync(CancellationToken ct = default)
    {
        var exe = BundledPath ?? throw new InvalidOperationException("yt-dlp isn't bundled with this build");
        var psi = new ProcessStartInfo(exe, "-U")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start yt-dlp");
        var output = await p.StandardOutput.ReadToEndAsync(ct) + await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return last ?? (p.ExitCode == 0 ? "yt-dlp is up to date" : $"yt-dlp exited with code {p.ExitCode}");
    }
}
