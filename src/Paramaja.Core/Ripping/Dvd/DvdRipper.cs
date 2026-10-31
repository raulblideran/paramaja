using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Paramaja.Core.Ripping.Dvd;

public enum DvdRipMode
{
    /// <summary>The VIDEO_TS folder as it is on the disc, decrypted: menus, extras, everything.</summary>
    Decrypt,
    /// <summary>One title as one MKV, nothing re-encoded.</summary>
    Mkv,
}

public sealed record DvdRipOptions
{
    /// <summary>A drive ("D:", "/dev/sr0"), an image (.iso) or a VIDEO_TS folder.</summary>
    public required string Source { get; init; }
    public DvdRipMode Mode { get; init; } = DvdRipMode.Mkv;
    /// <summary>The title for an MKV (DVD title numbers start at 1).</summary>
    public int Title { get; init; } = 1;
    /// <summary>The folder the rip goes in (the Videos folder).</summary>
    public required string OutputRoot { get; init; }
    public string? Name { get; init; }
    public string? Ffmpeg { get; init; }
    public string? Ffprobe { get; init; }
}

/// <param name="Output">The VIDEO_TS folder's parent, or the MKV.</param>
/// <param name="Problems">What didn't read (zero-filled), file by file.</param>
public sealed record DvdRipResult(string Output, IReadOnlyList<string> Problems, long Bytes);

/// <summary>
/// Rips a DVD: either its VIDEO_TS folder as it is, decrypted (libdvdread and libdvdcss, as
/// dvdbackup does), or one title as an MKV with every audio and subtitle track and the chapters,
/// nothing re-encoded (FFmpeg's DVD-Video reader, which uses the same libraries and also brings
/// the subtitles' palette and the streams' languages). The one conversion is LPCM audio, which
/// Matroska doesn't take as it is on DVDs: it becomes FLAC, bit for bit the same audio, as MakeMKV
/// does.
/// </summary>
public static class DvdRipper
{
    private const int ChunkBlocks = 512; // 1 MB

    // ---- Decrypt: the VIDEO_TS folder ---------------------------------------------------------

    public static Task<DvdRipResult> BackupAsync(DvdInfo info, DvdRipOptions o, IProgress<RipProgress>? progress = null,
        CancellationToken ct = default) => Task.Run(() => Backup(info, o, progress, ct), ct);

    private static DvdRipResult Backup(DvdInfo info, DvdRipOptions o, IProgress<RipProgress>? progress, CancellationToken ct)
    {
        var name = RipNames.Safe(o.Name ?? info.Name);
        var folder = RipNames.NewFolder(o.OutputRoot, name);
        var videoTs = Path.Combine(folder, "VIDEO_TS");
        Directory.CreateDirectory(videoTs);
        Directory.CreateDirectory(Path.Combine(folder, "AUDIO_TS"));
        var problems = new List<string>();
        var total = Math.Max(1, info.Files.Sum(f => f.Length));
        long done = 0;
        try
        {
            using var dvd = DvdRead.Open(o.Source);
            var buffer = new byte[ChunkBlocks * DvdRead.BlockBytes];
            // Title VOBs are one file to libdvdread, in parts on the disc: each part starts where the last ended.
            var partStarts = new Dictionary<(int Set, int Part), long>();
            foreach (var group in info.Files.Select(f => (f, Parse(f.Name))).Where(x => x.Item2 is { Domain: DvdRead.Domain.TitleVobs })
                         .GroupBy(x => x.Item2!.Value.Set))
            {
                long blocks = 0;
                foreach (var (file, which) in group.OrderBy(x => x.Item2!.Value.Part))
                {
                    partStarts[(group.Key, which!.Value.Part)] = blocks;
                    blocks += file.Length / DvdRead.BlockBytes;
                }
            }
            foreach (var (fileName, length) in info.Files.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (Parse(fileName) is not { } which)
                {
                    problems.Add($"{fileName}: not a DVD-Video file, left out");
                    continue;
                }
                progress?.Report(new RipProgress($"Copying {fileName}", 0, 0, done / (double)total));
                using var output = new FileStream(Path.Combine(videoTs, fileName), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
                using var file = dvd.OpenFile(which.Set, which.Domain);
                if (which.Domain is DvdRead.Domain.InfoFile or DvdRead.Domain.InfoBackupFile)
                {
                    var bytes = new byte[length];
                    if (file.ReadBytes(bytes) != length) problems.Add($"{fileName}: read short");
                    output.Write(bytes);
                    done += length;
                    continue;
                }
                var start = partStarts.GetValueOrDefault((which.Set, which.Part));
                var blocks = length / DvdRead.BlockBytes;
                var unreadable = 0L;
                for (long b = 0; b < blocks; b += ChunkBlocks)
                {
                    ct.ThrowIfCancellationRequested();
                    var n = (int)Math.Min(ChunkBlocks, blocks - b);
                    var span = buffer.AsSpan(0, n * DvdRead.BlockBytes);
                    unreadable += ReadChunk(file, (int)(start + b), n, span);
                    output.Write(span);
                    done += span.Length;
                    progress?.Report(new RipProgress($"Copying {fileName}", 0, 0, done / (double)total));
                }
                if (unreadable > 0) problems.Add($"{fileName}: {unreadable} unreadable blocks, filled with zeros");
            }
            WriteLog(folder, info, o, problems);
            return new DvdRipResult(folder, problems, done);
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    /// <summary>
    /// A chunk of blocks, decrypted; after a short read it's tried again, then block by block, and
    /// a block that never reads is left as zeros. Returns how many were left so.
    /// </summary>
    private static int ReadChunk(DvdRead.File file, int offset, int blocks, Span<byte> buffer)
    {
        for (var attempt = 0; attempt < 2; attempt++)
            if (file.ReadBlocks(offset, buffer) == blocks) return 0;
        var bad = 0;
        for (var i = 0; i < blocks; i++)
        {
            var block = buffer.Slice(i * DvdRead.BlockBytes, DvdRead.BlockBytes);
            var ok = false;
            for (var attempt = 0; attempt < 3 && !ok; attempt++) ok = file.ReadBlocks(offset + i, block) == 1;
            if (!ok)
            {
                block.Clear();
                bad++;
            }
        }
        return bad;
    }

    /// <summary>Which libdvdread file a VIDEO_TS file is: VIDEO_TS.IFO/BUP/VOB (set 0), VTS_nn_0.IFO/BUP/VOB, VTS_nn_k.VOB (k from 1).</summary>
    internal static (int Set, DvdRead.Domain Domain, int Part)? Parse(string name)
    {
        name = name.ToUpperInvariant();
        switch (name)
        {
            case "VIDEO_TS.IFO": return (0, DvdRead.Domain.InfoFile, 0);
            case "VIDEO_TS.BUP": return (0, DvdRead.Domain.InfoBackupFile, 0);
            case "VIDEO_TS.VOB": return (0, DvdRead.Domain.MenuVobs, 0);
        }
        if (name.Length != 12 || !name.StartsWith("VTS_", StringComparison.Ordinal) || name[6] != '_' || name[8] != '.'
            || !int.TryParse(name.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var set) || set is < 1 or > 99
            || !char.IsAsciiDigit(name[7]))
            return null;
        var part = name[7] - '0';
        return name[9..] switch
        {
            "IFO" when part == 0 => (set, DvdRead.Domain.InfoFile, 0),
            "BUP" when part == 0 => (set, DvdRead.Domain.InfoBackupFile, 0),
            "VOB" when part == 0 => (set, DvdRead.Domain.MenuVobs, 0),
            "VOB" => (set, DvdRead.Domain.TitleVobs, part),
            _ => null,
        };
    }

    private static void WriteLog(string folder, DvdInfo info, DvdRipOptions o, IReadOnlyList<string> problems)
    {
        var b = new StringBuilder();
        b.Append("Paramaja — DVD backup (VIDEO_TS, decrypted)\r\n\r\n");
        b.Append($"Disc   : {info.Name}\r\nSource : {o.Source}\r\nDate   : {DateTime.Now.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)}\r\n\r\n");
        foreach (var t in info.Titles) b.Append(t.Label).Append("\r\n");
        b.Append("\r\n").Append(problems.Count == 0 ? "Every block read.\r\n" : string.Join("\r\n", problems) + "\r\n");
        File.WriteAllText(Path.Combine(folder, "Paramaja rip.log"), b.ToString(), new UTF8Encoding(true));
    }

    // ---- MKV: one title, nothing re-encoded -------------------------------------------------------

    private static bool? _dvdvideo;

    /// <summary>This ffmpeg reads DVDs (its dvdvideo demuxer: FFmpeg 7 and up, built with libdvdread and libdvdnav).</summary>
    public static async Task<bool> FfmpegReadsDvdsAsync(string ffmpeg, CancellationToken ct = default)
    {
        if (_dvdvideo is { } known) return known;
        var output = new StringBuilder();
        try { await RipTools.RunAsync(ffmpeg, ["-hide_banner", "-demuxers"], ct, line => output.AppendLine(line)).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { return false; }
        return (_dvdvideo = output.ToString().Contains(" dvdvideo ", StringComparison.Ordinal)).Value;
    }

    /// <summary>FFmpeg's reading options: the title and angle, and for a folder or image the exact chapter marks (a second, quick pass over it).</summary>
    internal static IEnumerable<string> InputArguments(DvdRipOptions o, bool preindex)
    {
        yield return "-f";
        yield return "dvdvideo";
        yield return "-title";
        yield return o.Title.ToString(CultureInfo.InvariantCulture);
        yield return "-angle";
        yield return "1";
        if (preindex)
        {
            yield return "-preindex";
            yield return "1";
        }
        yield return "-i";
        yield return o.Source;
    }

    /// <param name="lpcm">Which of the title's audio tracks (counted among audio, from 0) are LPCM.</param>
    internal static IEnumerable<string> MkvArguments(DvdRipOptions o, bool preindex, IReadOnlyList<int> lpcm, string title, string output)
    {
        yield return "-hide_banner";
        yield return "-nostdin";
        yield return "-loglevel";
        yield return "error";
        yield return "-nostats";
        yield return "-progress";
        yield return "pipe:1";
        foreach (var a in InputArguments(o, preindex)) yield return a;
        yield return "-map";
        yield return "0";
        yield return "-c";
        yield return "copy";
        foreach (var k in lpcm)
        {
            yield return $"-c:a:{k}";
            yield return "flac";
            yield return $"-compression_level:a:{k}";
            yield return "8";
        }
        yield return "-metadata";
        yield return $"title={title}";
        yield return "-f";
        yield return "matroska";
        yield return output;
    }

    /// <summary>The title's streams as FFmpeg reads them: (type, codec) in order, and its length in seconds.</summary>
    internal static (List<(string Type, string Codec)> Streams, double Duration) ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var streams = new List<(string, string)>();
        if (doc.RootElement.TryGetProperty("streams", out var list))
            foreach (var s in list.EnumerateArray())
                streams.Add((s.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "",
                    s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : ""));
        var duration = doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
        return (streams, duration);
    }

    public static async Task<DvdRipResult> RemuxAsync(DvdInfo info, DvdRipOptions o, IProgress<RipProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (o.Ffmpeg is not { } ffmpeg || o.Ffprobe is not { } ffprobe) throw new InvalidOperationException("FFmpeg isn't installed");
        if (!await FfmpegReadsDvdsAsync(ffmpeg, ct).ConfigureAwait(false))
            throw new InvalidOperationException("this FFmpeg can't read DVDs (it needs its DVD-Video reader)");
        var title = info.Titles.FirstOrDefault(t => t.Number == o.Title) ?? throw new ArgumentException($"the disc has no title {o.Title}");
        // A drive is read once (a second pass over a disc is slow); a folder or image gets exact chapter marks.
        var preindex = Directory.Exists(o.Source) || (File.Exists(o.Source) && !o.Source.StartsWith("/dev/", StringComparison.Ordinal));
        progress?.Report(new RipProgress("Reading the title", 0, 0, 0));
        var probe = new StringBuilder();
        await RipTools.RunAsync(ffprobe,
            ["-v", "error", .. InputArguments(o, preindex: false), "-show_entries", "stream=codec_type,codec_name:format=duration", "-of", "json"],
            ct, line => probe.AppendLine(line)).ConfigureAwait(false);
        var (streams, duration) = ParseProbe(probe.ToString());
        var lpcm = streams.Where(s => s.Type == "audio").Select((s, i) => (s.Codec, i)).Where(x => x.Codec.StartsWith("pcm", StringComparison.Ordinal))
            .Select(x => x.i).ToList();
        if (duration <= 0) duration = title.Duration.TotalSeconds;

        var name = RipNames.Safe(o.Name ?? info.Name);
        var main = info.MainTitle?.Number == title.Number;
        Directory.CreateDirectory(o.OutputRoot);
        var output = RipNames.NewFile(o.OutputRoot, main ? name : $"{name} - Title {title.Number}", ".mkv");
        try
        {
            await RipTools.RunAsync(ffmpeg, MkvArguments(o, preindex, lpcm, o.Name ?? info.Name, output), ct, line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && duration > 0)
                    progress?.Report(new RipProgress($"Writing title {title.Number}", 0, 0, Math.Clamp(us / 1e6 / duration, 0, 1)));
            }).ConfigureAwait(false);
            return new DvdRipResult(output, [], new FileInfo(output).Length);
        }
        catch
        {
            try { File.Delete(output); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
