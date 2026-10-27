using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

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

    /// <summary>
    /// yt-dlp's format choice: the best video up to <paramref name="maxHeight"/> (any height
    /// for 0) plus the best audio, falling back to the best overall if nothing is that small.
    /// </summary>
    public static string Format(int maxHeight) => maxHeight > 0
        ? $"bv*[height<={maxHeight}]+ba/b[height<={maxHeight}]/bv*+ba/b"
        : "bv*+ba/b";

    /// <summary>A video's title and the resolutions it's offered in, highest first.</summary>
    public sealed record VideoInfo(string Title, IReadOnlyList<int> Heights);

    /// <summary>Asks yt-dlp (<c>-J</c>) which resolutions a page's video comes in, without downloading it.</summary>
    public async Task<VideoInfo> GetInfoAsync(string url, CancellationToken ct = default)
    {
        var exe = BundledPath ?? throw new InvalidOperationException("yt-dlp isn't bundled with this build");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-J", "--no-playlist" }) psi.ArgumentList.Add(a);
        if (BundledJsRuntime is { } js) { psi.ArgumentList.Add("--js-runtimes"); psi.ArgumentList.Add("quickjs:" + js); }
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(url);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start yt-dlp");
        // A lookup that's no longer wanted (another link was pasted) stops yt-dlp too.
        await using var kill = ct.Register(() =>
        {
            try { p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stderr = p.StandardError.ReadToEndAsync(ct);
        var json = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            var error = (await stderr).Split('\n').LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
            throw new InvalidOperationException(error?["ERROR:".Length..].Trim() ?? $"yt-dlp exited with code {p.ExitCode}");
        }
        return ParseInfo(json);
    }

    internal static VideoInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
            {
                // Audio-only formats have vcodec "none"; storyboards and the like have no height.
                var video = !f.TryGetProperty("vcodec", out var vc) || vc.ValueKind != JsonValueKind.String || vc.GetString() != "none";
                if (video && f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && h.GetInt32() > 0)
                    heights.Add(h.GetInt32());
            }
        }
        else if (root.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && h.GetInt32() > 0)
        {
            heights.Add(h.GetInt32()); // a site with a single file
        }
        return new VideoInfo(title, [.. heights]);
    }

    /// <summary>Embedding a thumbnail as cover art needs ffprobe beside ffmpeg; without it, no cover.</summary>
    public string? BundledFfprobe =>
        File.Exists(Path.Combine(appDirectory, Exe("ffprobe"))) ? Path.Combine(appDirectory, Exe("ffprobe")) : null;

    /// <summary>Merges the separate video and audio streams of a download (YouTube serves HD that way).</summary>
    public string? BundledFfmpeg =>
        File.Exists(Path.Combine(appDirectory, Exe("ffmpeg"))) ? Path.Combine(appDirectory, Exe("ffmpeg")) : null;

    /// <summary>
    /// Downloads the best video and audio of a page into <paramref name="folder"/>, merged into
    /// one MKV, or with <paramref name="audioOnly"/> just its best audio stream as it is (no
    /// re-encoding). Returns the file's path. Cancelling stops yt-dlp and deletes its partial files.
    /// </summary>
    /// <param name="maxHeight">The highest video resolution wanted (e.g. 1080); 0 for the best.</param>
    public async Task<string> DownloadAsync(string url, string folder, Action<DownloadProgress>? progress = null,
        CancellationToken ct = default, int maxHeight = 0, bool audioOnly = false)
    {
        var exe = BundledPath ?? throw new InvalidOperationException("yt-dlp isn't bundled with this build");
        Directory.CreateDirectory(folder);
        var (planned, height) = await PlanAsync(exe, url, folder, maxHeight, audioOnly, ct);
        // The audio's extension is only known once yt-dlp has extracted it (.opus, .m4a…).
        var fileName = audioOnly ? UniqueStem(folder, Path.GetFileNameWithoutExtension(planned)) : UniqueFileName(folder, planned, height);
        var template = fileName.Replace("%", "%%") + (audioOnly ? ".%(ext)s" : "");
        var printed = Path.Combine(Path.GetTempPath(), $"paramaja-download-{Guid.NewGuid():N}.txt");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // yt-dlp reads -o as a template: a literal % in the title has to be doubled.
        foreach (var a in DownloadArguments(url, folder, printed, maxHeight, template, audioOnly)) psi.ArgumentList.Add(a);

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

    /// <summary>
    /// Asks yt-dlp, without downloading, what the file would be called and which resolution it
    /// would get, so an existing download isn't skipped or overwritten (see UniqueFileName).
    /// </summary>
    private async Task<(string FileName, int? Height)> PlanAsync(string exe, string url, string folder, int maxHeight,
        bool audioOnly, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in DownloadArguments(url, folder, "", maxHeight, audioOnly: audioOnly)
                     .Where(a => a is not "--print-to-file" and not "after_move:filepath" and not ""))
            psi.ArgumentList.Add(a);
        // Insert before "--" and the URL, which come last.
        var at = psi.ArgumentList.IndexOf("--");
        foreach (var a in new[] { "-s", "--print", "%(height)s", "--print", "filename" }.Reverse()) psi.ArgumentList.Insert(at, a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start yt-dlp");
        var stderr = p.StandardError.ReadToEndAsync(ct);
        var lines = (await p.StandardOutput.ReadToEndAsync(ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 || lines.Length < 2)
        {
            var error = (await stderr).Split('\n').LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
            throw new InvalidOperationException(error?["ERROR:".Length..].Trim() ?? $"yt-dlp exited with code {p.ExitCode}");
        }
        return (Path.GetFileName(lines[^1]), int.TryParse(lines[^2], out var h) ? h : null);
    }

    /// <summary>
    /// The name to save under: yt-dlp's own name if it's free; else with the resolution added
    /// ("Title [id] 720p.mkv"); and if that exists too, numbered ("Title [id] 720p (1).mkv").
    /// </summary>
    internal static string UniqueFileName(string folder, string fileName, int? height)
    {
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        bool Free(string name) => !File.Exists(Path.Combine(folder, name));
        if (Free(fileName)) return fileName;
        var withHeight = height is > 0 ? $"{stem} {height}p" : stem;
        if (Free(withHeight + ext)) return withHeight + ext;
        for (var n = 1; ; n++)
            if (Free($"{withHeight} ({n}){ext}")) return $"{withHeight} ({n}){ext}";
    }

    /// <summary>
    /// An audio download's name without its extension: yt-dlp's own if no file of that name
    /// exists with any extension, else numbered ("Title [id] (1)").
    /// </summary>
    internal static string UniqueStem(string folder, string stem)
    {
        bool Free(string name) => !Directory.Exists(folder)
            || !Directory.EnumerateFiles(folder).Any(f => Path.GetFileNameWithoutExtension(f) == name);
        if (Free(stem)) return stem;
        for (var n = 1; ; n++)
            if (Free($"{stem} ({n})")) return $"{stem} ({n})";
    }

    /// <summary>The best audio stream on its own; a site with only combined files gives its audio.</summary>
    public const string AudioFormat = "ba/b";

    internal IEnumerable<string> DownloadArguments(string url, string folder, string printFile, int maxHeight = 0,
        string outputTemplate = "%(title).150B [%(id)s].%(ext)s", bool audioOnly = false)
    {
        // A watch link that is also in a playlist means the one video.
        yield return "--no-playlist";
        yield return "-f";
        if (audioOnly)
        {
            yield return AudioFormat;
            // Out of its video container into an audio file, keeping the stream as it is:
            // "best" never re-encodes (Opus stays Opus, AAC becomes .m4a).
            yield return "-x";
            yield return "--audio-format";
            yield return "best";
        }
        else
        {
            yield return Format(maxHeight);
            yield return "--merge-output-format";
            yield return "mkv";
        }
        // Title, uploader, date, description and link as tags, and the chapters, written by the
        // bundled ffmpeg. The thumbnail goes in as JPEG cover art where ffprobe is bundled too.
        yield return "--embed-metadata";
        yield return "--embed-chapters";
        if (BundledFfprobe is not null)
        {
            yield return "--embed-thumbnail";
            yield return "--convert-thumbnails";
            yield return "jpg";
        }
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
        yield return outputTemplate;
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
