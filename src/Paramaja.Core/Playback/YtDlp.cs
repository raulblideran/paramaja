using System.Diagnostics;
using System.Globalization;

namespace Paramaja.Core.Playback;

/// <summary>One progress line of a download. Video and audio are fetched one after the other.</summary>
public sealed record DownloadProgress(long Downloaded, long? Total, double? BytesPerSecond, int? EtaSeconds, bool IsAudio)
{
    public double? Percent => Total is > 0 ? 100.0 * Downloaded / Total.Value : null;

    public override string ToString()
    {
        var parts = new List<string> { IsAudio ? "Downloading audio" : "Downloading video" };
        if (Percent is { } p) parts.Add($"{p:0}%");
        if (BytesPerSecond is { } bps) parts.Add($"{TimeFormat.Bytes((long)bps)}/s");
        if (EtaSeconds is { } eta) parts.Add($"{TimeFormat.Format(eta)} left");
        return string.Join(" · ", parts);
    }

    internal const string Prefix = "PARAMAJA ";

    /// <summary>yt-dlp's progress template; fields are raw numbers ("NA" when unknown).</summary>
    internal const string Template =
        "download:" + Prefix + "%(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.vcodec)s";

    internal static DownloadProgress? Parse(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var f = line[Prefix.Length..].Split('|');
        if (f.Length < 5 || Number(f[0]) is not { } done) return null;
        return new DownloadProgress((long)done, (long?)Number(f[1]), Number(f[2]), (int?)Number(f[3]), f[4].Trim() == "none");
    }

    private static double? Number(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}

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

    /// <summary>Merges the separate video and audio streams of a download (YouTube serves HD that way).</summary>
    public string? BundledFfmpeg =>
        File.Exists(Path.Combine(appDirectory, Exe("ffmpeg"))) ? Path.Combine(appDirectory, Exe("ffmpeg")) : null;

    /// <summary>
    /// Downloads the best video and audio of a page into <paramref name="folder"/>, merged into
    /// one MKV. Returns the file's path. Cancelling stops yt-dlp and deletes its partial files.
    /// </summary>
    public async Task<string> DownloadAsync(string url, string folder, Action<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var exe = BundledPath ?? throw new InvalidOperationException("yt-dlp isn't bundled with this build");
        Directory.CreateDirectory(folder);
        var printed = Path.Combine(Path.GetTempPath(), $"paramaja-download-{Guid.NewGuid():N}.txt");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in DownloadArguments(url, folder, printed)) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start yt-dlp");
        var partials = new List<string>();
        string? error = null;
        var stderr = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
                if (line.StartsWith("ERROR:", StringComparison.Ordinal)) error = line["ERROR:".Length..].Trim();
        }, CancellationToken.None);
        try
        {
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (DownloadProgress.Parse(line) is { } d) progress?.Invoke(d);
                else if (line.StartsWith(DestinationPrefix, StringComparison.Ordinal)) partials.Add(line[DestinationPrefix.Length..].Trim());
            }
            await p.WaitForExitAsync(ct);
            await stderr;
            if (p.ExitCode != 0) throw new InvalidOperationException(error ?? $"yt-dlp exited with code {p.ExitCode}");
            var file = File.Exists(printed) ? File.ReadLines(printed).LastOrDefault(l => l.Length > 0)?.Trim() : null;
            return file ?? throw new InvalidOperationException("yt-dlp didn't say where it saved the file");
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); await p.WaitForExitAsync(CancellationToken.None); }
            catch (InvalidOperationException) { }
            foreach (var f in partials.SelectMany(f => new[] { f, f + ".part", f + ".ytdl" }))
            {
                try { File.Delete(f); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
        finally
        {
            try { File.Delete(printed); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private const string DestinationPrefix = "[download] Destination:";

    internal IEnumerable<string> DownloadArguments(string url, string folder, string printFile)
    {
        // A watch link that is also in a playlist means the one video.
        yield return "--no-playlist";
        yield return "-f";
        yield return "bv*+ba/b";
        yield return "--merge-output-format";
        yield return "mkv";
        if (BundledFfmpeg is { } ffmpeg) { yield return "--ffmpeg-location"; yield return ffmpeg; }
        if (BundledJsRuntime is { } js) { yield return "--js-runtimes"; yield return "quickjs:" + js; }
        yield return "--newline";
        yield return "--progress-template";
        yield return DownloadProgress.Template;
        yield return "--print-to-file";
        yield return "after_move:filepath";
        yield return printFile;
        yield return "-P";
        yield return folder;
        yield return "-o";
        yield return "%(title).150B [%(id)s].%(ext)s";
        yield return "--";
        yield return url;
    }

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
