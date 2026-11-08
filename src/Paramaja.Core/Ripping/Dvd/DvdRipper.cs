using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Paramaja.Core.Ripping.Dvd;

/// <summary>Chapters next to each other, read in one go (1-based, inclusive).</summary>
public readonly record struct ChapterRun(int From, int To);

public enum DvdRipMode
{
    /// <summary>The VIDEO_TS folder as it is on the disc, decrypted: menus, extras, everything.</summary>
    Decrypt,
    /// <summary>One title as one MKV: its video copied (<see cref="DvdVideo.Copy"/>) or encoded.</summary>
    Mkv,
}

/// <summary>
/// An MKV's video: the disc's own, or encoded to look the same in less space. Interlaced video is
/// deinterlaced to 50p (59.94p) with BWDIF, as Paramaja plays DVDs, for both encoders: each field
/// becomes a frame, so the motion stays as smooth as on the disc. Settings from trials on a PAL
/// concert DVD (576i), against the disc's frames (see CHANGELOG 1.11.0 and 1.12.0).
/// </summary>
public enum DvdVideo
{
    /// <summary>The disc's MPEG-2 as it is: lossless.</summary>
    Copy,
    /// <summary>H.264 (x264, CRF 16, preset slow; CRF 14 for interlaced video, deinterlaced to 50p with BWDIF).</summary>
    X264,
    /// <summary>
    /// H.265 (x265, CRF 18, preset slow; CRF 16 for interlaced video, deinterlaced to 50p with
    /// BWDIF). HEVC has no interlaced mode players handle: x265's field coding comes back with the
    /// field order flag swapping every frame.
    /// </summary>
    X265,
}

/// <summary>How a title's video is scanned, which the encoders need to know.</summary>
public enum VideoScan { Progressive, TopFieldFirst, BottomFieldFirst }

public sealed record DvdRipOptions
{
    /// <summary>A drive ("D:", "/dev/sr0"), an image (.iso) or a VIDEO_TS folder.</summary>
    public required string Source { get; init; }
    public DvdRipMode Mode { get; init; } = DvdRipMode.Mkv;
    public DvdVideo Video { get; init; } = DvdVideo.Copy;
    /// <summary>The title for an MKV (DVD title numbers start at 1).</summary>
    public int Title { get; init; } = 1;
    /// <summary>The title's chapters to rip (1-based); null (or all of them) for the whole title.</summary>
    public IReadOnlyList<int>? Chapters { get; init; }
    /// <summary>Each chapter as its own MKV, in a folder; otherwise the chapters are one MKV.</summary>
    public bool ChapterFiles { get; init; }
    /// <summary>The folder the rip goes in (the Videos folder).</summary>
    public required string OutputRoot { get; init; }
    public string? Name { get; init; }
    public string? Ffmpeg { get; init; }
    public string? Ffprobe { get; init; }
}

/// <param name="Output">The VIDEO_TS folder's parent, the MKV, or the folder of a chapter-per-file rip.</param>
/// <param name="Problems">What didn't read (zero-filled), file by file.</param>
public sealed record DvdRipResult(string Output, IReadOnlyList<string> Problems, long Bytes);

/// <summary>
/// Rips a DVD: either its VIDEO_TS folder as it is, decrypted (libdvdread and libdvdcss, as
/// dvdbackup does), or one title as an MKV with every audio and subtitle track and the chapters
/// (FFmpeg's DVD-Video reader, which uses the same libraries and also brings the subtitles'
/// palette and the streams' languages). The audio and subtitles are never re-encoded; the video is
/// copied, or encoded with x264 or x265 when asked (<see cref="DvdVideo"/>). The one audio
/// conversion is LPCM, which Matroska doesn't take as it is on DVDs: it becomes FLAC, bit for bit
/// the same audio, as MakeMKV does.
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

    private static readonly Dictionary<string, HashSet<string>> _encoders = [];

    /// <summary>The video encoders of this ffmpeg the MKVs can use: libx264, libx265 (none if it won't run).</summary>
    public static async Task<IReadOnlySet<string>> FfmpegEncodersAsync(string ffmpeg, CancellationToken ct = default)
    {
        lock (_encoders)
            if (_encoders.TryGetValue(ffmpeg, out var known)) return known;
        var output = new StringBuilder();
        try { await RipTools.RunAsync(ffmpeg, ["-hide_banner", "-encoders"], ct, line => output.AppendLine(line)).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { return new HashSet<string>(); }
        var found = ParseEncoders(output.ToString());
        lock (_encoders) _encoders[ffmpeg] = found;
        return found;
    }

    /// <summary>The encoders in <c>ffmpeg -encoders</c>' list (" V....D libx264  …").</summary>
    internal static HashSet<string> ParseEncoders(string list) =>
        list.Split('\n').Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Length >= 2 && w[0].Length == 6 && w[0][0] == 'V').Select(w => w[1])
            .Where(n => n is "libx264" or "libx265").ToHashSet();

    public static string EncoderFor(DvdVideo video) => video == DvdVideo.X265 ? "libx265" : "libx264";

    /// <summary>
    /// FFmpeg's reading options: the title and angle, and for a folder or image the exact chapter
    /// marks (a second, quick pass over it). <paramref name="chapter"/> starts reading at a chapter
    /// (the picture's sample); <paramref name="range"/> reads only those chapters.
    /// </summary>
    internal static IEnumerable<string> InputArguments(DvdRipOptions o, bool preindex, int? chapter = null, ChapterRun? range = null)
    {
        yield return "-f";
        yield return "dvdvideo";
        yield return "-title";
        yield return o.Title.ToString(CultureInfo.InvariantCulture);
        yield return "-angle";
        yield return "1";
        if (range is { } r)
        {
            yield return "-chapter_start";
            yield return r.From.ToString(CultureInfo.InvariantCulture);
            yield return "-chapter_end";
            yield return r.To.ToString(CultureInfo.InvariantCulture);
        }
        else if (chapter is { } c)
        {
            yield return "-chapter_start";
            yield return c.ToString(CultureInfo.InvariantCulture);
        }
        if (preindex)
        {
            yield return "-preindex";
            yield return "1";
        }
        yield return "-i";
        yield return o.Source;
    }

    /// <param name="lpcm">Which of the title's audio tracks (counted among audio, from 0) are LPCM.</param>
    /// <param name="scan">How the video is scanned, for x264 and x265 (copied video stays as it is).</param>
    /// <param name="range">Only these chapters; null for the whole title.</param>
    /// <param name="partBits">
    /// For a part to be joined: each LPCM track's bits (16, 20, 24), kept as PCM of that depth.
    /// FLAC parts don't join (the concat reader keeps the first part's FLAC header, which the next
    /// part's frames don't fit), so the joined file is encoded to FLAC once, over all of it.
    /// </param>
    internal static IEnumerable<string> MkvArguments(DvdRipOptions o, bool preindex, IReadOnlyList<int> lpcm, string title, string output,
        VideoScan scan = VideoScan.Progressive, ChapterRun? range = null, IReadOnlyList<int>? partBits = null)
    {
        yield return "-hide_banner";
        yield return "-nostdin";
        yield return "-loglevel";
        yield return "error";
        yield return "-nostats";
        yield return "-progress";
        yield return "pipe:1";
        foreach (var a in InputArguments(o, preindex, range: range)) yield return a;
        yield return "-map";
        yield return "0";
        yield return "-c";
        yield return "copy";
        for (var i = 0; i < lpcm.Count; i++)
        {
            var k = lpcm[i];
            yield return $"-c:a:{k}";
            if (partBits is not null)
            {
                yield return i < partBits.Count && partBits[i] > 16 ? "pcm_s24le" : "pcm_s16le";
                continue;
            }
            yield return "flac";
            yield return $"-compression_level:a:{k}";
            yield return "8";
        }
        foreach (var a in VideoArguments(o.Video, scan)) yield return a;
        yield return "-metadata";
        yield return $"title={title}";
        yield return "-f";
        yield return "matroska";
        yield return output;
    }

    /// <summary>
    /// The video's options after <c>-c copy</c>: nothing for a copy, else the encoder's. Interlaced
    /// video goes through BWDIF first, a frame per field (50p / 59.94p), in the field order the
    /// title was found to have.
    /// </summary>
    internal static IEnumerable<string> VideoArguments(DvdVideo video, VideoScan scan)
    {
        if (video == DvdVideo.Copy) yield break;
        var interlaced = scan is VideoScan.TopFieldFirst or VideoScan.BottomFieldFirst;
        if (interlaced)
        {
            yield return "-filter:v";
            yield return $"bwdif=mode=send_field:parity={(scan == VideoScan.TopFieldFirst ? "tff" : "bff")}:deint=all";
        }
        // 50p from fields is harder to keep than the frames it came from: x264 needs CRF 14 there to
        // look as close to it as x265 does at 16 (VMAF 97.0 and 97.3 on a minute of a PAL concert).
        string[] encoder = video == DvdVideo.X264
            ? ["-c:v", "libx264", "-preset", "slow", "-crf", interlaced ? "14" : "16"]
            : ["-c:v", "libx265", "-preset", "slow", "-crf", interlaced ? "16" : "18", "-x265-params", "no-sao=1:aq-mode=3:log-level=error"];
        foreach (var a in encoder) yield return a;
    }

    // ---- Chapters -----------------------------------------------------------------------------

    /// <summary>
    /// The chapters asked for, sorted, within the title; null for the whole title (nothing asked,
    /// or every chapter), which is read as it always was.
    /// </summary>
    internal static IReadOnlyList<int>? Selection(DvdRipOptions o, DvdTitle title)
    {
        if (o.Chapters is null) return null;
        var picked = o.Chapters.Where(c => c >= 1 && c <= title.Chapters).Distinct().Order().ToList();
        if (picked.Count == 0) throw new ArgumentException("no chapters picked");
        return picked.Count == title.Chapters && !o.ChapterFiles ? null : picked;
    }

    /// <summary>Chapters next to each other, as runs FFmpeg reads in one go (2, 3, 4, 7 → 2-4, 7).</summary>
    internal static IReadOnlyList<ChapterRun> Runs(IReadOnlyList<int> chapters)
    {
        var runs = new List<ChapterRun>();
        foreach (var c in chapters)
            if (runs.Count > 0 && runs[^1].To == c - 1) runs[^1] = runs[^1] with { To = c };
            else runs.Add(new ChapterRun(c, c));
        return runs;
    }

    /// <summary>What the file's name says it holds: "Chapter 3", "Chapters 3-5", "Chapters 2, 4-6, 9".</summary>
    internal static string ChaptersLabel(IReadOnlyList<int> chapters)
    {
        var runs = Runs(chapters);
        if (chapters.Count == 1) return $"Chapter {chapters[0]}";
        return "Chapters " + string.Join(", ", runs.Select(r => r.From == r.To ? $"{r.From}" : $"{r.From}-{r.To}"));
    }

    /// <summary>
    /// The chapter marks of parts joined end to end, as an FFmpeg metadata file: each part's own
    /// marks moved by the length of the parts before it, or one mark for a part without any
    /// (a single chapter). Each is named after its chapter on the disc.
    /// </summary>
    internal static string JoinedChapters(string title, IReadOnlyList<(ChapterRun Run, double Duration, IReadOnlyList<(double Start, double End)> Marks)> parts)
    {
        static string Escape(string v) => Regex.Replace(v, @"[=;#\\\n]", m => "\\" + m.Value);
        static string Ms(double s) => Math.Round(s * 1000).ToString(CultureInfo.InvariantCulture);
        var b = new StringBuilder(";FFMETADATA1\n").Append("title=").Append(Escape(title)).Append('\n');
        double offset = 0;
        foreach (var (run, duration, marks) in parts)
        {
            // A part's own marks when it has one per chapter (a run, read with its index), else one for it all.
            IReadOnlyList<(double Start, double End)> spans = marks.Count == run.To - run.From + 1 ? marks : [(0, duration)];
            // Measured from the part's first mark, whatever time its first frame had.
            var zero = spans[0].Start;
            spans = spans.Select(m => (m.Start - zero, m.End - zero)).ToList();
            for (var i = 0; i < spans.Count; i++)
            {
                var label = spans.Count == 1 && run.From != run.To ? $"Chapters {run.From}-{run.To}" : $"Chapter {run.From + i}";
                var end = i + 1 < spans.Count ? spans[i + 1].Start : duration;
                b.Append("[CHAPTER]\nTIMEBASE=1/1000\n")
                    .Append("START=").Append(Ms(offset + spans[i].Start)).Append('\n')
                    .Append("END=").Append(Ms(offset + Math.Max(spans[i].Start, end))).Append('\n')
                    .Append("title=").Append(Escape(label)).Append('\n');
            }
            offset += duration;
        }
        return b.ToString();
    }

    /// <summary>A part's length and chapter marks (seconds), from <c>ffprobe -show_chapters -show_entries format=duration -of json</c>.</summary>
    internal static (double Duration, List<(double Start, double End)> Marks) ParseParts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var marks = new List<(double, double)>();
        static double Seconds(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        if (doc.RootElement.TryGetProperty("chapters", out var list))
            foreach (var c in list.EnumerateArray())
                marks.Add((Seconds(c, "start_time"), Seconds(c, "end_time")));
        var duration = doc.RootElement.TryGetProperty("format", out var f) ? Seconds(f, "duration") : 0;
        return (duration, marks);
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

    /// <summary>Each audio stream's bits per sample as ffprobe tells it (bits_per_raw_sample; 0 when it doesn't).</summary>
    internal static List<int> ParseAudioBits(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var bits = new List<int>();
        if (doc.RootElement.TryGetProperty("streams", out var list))
            foreach (var s in list.EnumerateArray())
                if (s.TryGetProperty("codec_type", out var t) && t.GetString() == "audio")
                    bits.Add(s.TryGetProperty("bits_per_raw_sample", out var b) && int.TryParse(b.GetString(), out var n) ? n : 0);
        return bits;
    }

    /// <summary>The first video stream's field order as ffprobe tells it ("tt", "progressive"…), if it does.</summary>
    internal static string? ParseFieldOrder(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("streams", out var list)) return null;
        foreach (var s in list.EnumerateArray())
            if (s.TryGetProperty("codec_type", out var t) && t.GetString() == "video")
                return s.TryGetProperty("field_order", out var f) ? f.GetString() : null;
        return null;
    }

    /// <summary>
    /// What the frames themselves look like: FFmpeg's idet over a sample, its multi-frame counts
    /// summed (a filter graph set up twice prints twice). Null when it couldn't tell (black, titles).
    /// </summary>
    internal static VideoScan? ParseIdet(string log)
    {
        long tff = 0, bff = 0, progressive = 0;
        foreach (var line in log.Split('\n'))
        {
            var at = line.IndexOf("Multi frame detection:", StringComparison.Ordinal);
            if (at < 0) continue;
            var words = line[(at + 22)..].Split([' ', ':'], StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i + 1 < words.Length; i += 2)
                if (long.TryParse(words[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    switch (words[i])
                    {
                        case "TFF": tff += n; break;
                        case "BFF": bff += n; break;
                        case "Progressive": progressive += n; break;
                    }
        }
        if (tff + bff + progressive == 0) return null;
        if (progressive >= tff + bff) return VideoScan.Progressive;
        return tff >= bff ? VideoScan.TopFieldFirst : VideoScan.BottomFieldFirst;
    }

    /// <summary>
    /// The flags say, the picture confirms: plenty of films are flagged interlaced but progressive.
    /// Flags that say progressive are taken as they are; flagged interlaced, the sample decides,
    /// unless it couldn't tell.
    /// </summary>
    internal static VideoScan Decide(string? fieldOrder, VideoScan? sample)
    {
        VideoScan? flagged = fieldOrder switch
        {
            "tt" or "tb" => VideoScan.TopFieldFirst,
            "bb" or "bt" => VideoScan.BottomFieldFirst,
            _ => null,
        };
        if (flagged is null) return VideoScan.Progressive;
        return sample ?? flagged.Value;
    }

    /// <summary>A third of the way in: past the opening titles, where the picture is the film's own.</summary>
    internal static int SampleChapter(int chapters) => Math.Max(1, (chapters + 2) / 3);

    /// <summary>The same, among the chapters being ripped.</summary>
    internal static int SampleChapter(IReadOnlyList<int> chapters) => chapters[SampleChapter(chapters.Count) - 1];

    private static async Task<VideoScan> ScanAsync(string ffmpeg, DvdRipOptions o, int sample, string? fieldOrder, CancellationToken ct)
    {
        if (Decide(fieldOrder, null) == VideoScan.Progressive) return VideoScan.Progressive;
        var log = new StringBuilder();
        try
        {
            await RipTools.RunAsync(ffmpeg,
                ["-hide_banner", "-nostdin", "-nostats", .. InputArguments(o, preindex: false, sample),
                    "-t", "20", "-map", "0:v:0", "-vf", "idet", "-an", "-sn", "-f", "null", "-"],
                ct, onStderr: line => log.AppendLine(line)).ConfigureAwait(false);
        }
        catch (IOException) { } // the flags alone, then
        return Decide(fieldOrder, ParseIdet(log.ToString()));
    }

    /// <summary>
    /// A title as an MKV, or some of its chapters: one MKV of them (chapters next to each other
    /// read in one go, others ripped apart and joined, their marks kept), or one MKV per chapter
    /// in a folder. Whatever was written is deleted if the rip fails or is cancelled.
    /// </summary>
    public static async Task<DvdRipResult> RemuxAsync(DvdInfo info, DvdRipOptions o, IProgress<RipProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (o.Ffmpeg is not { } ffmpeg || o.Ffprobe is not { } ffprobe) throw new InvalidOperationException("FFmpeg isn't installed");
        if (!await FfmpegReadsDvdsAsync(ffmpeg, ct).ConfigureAwait(false))
            throw new InvalidOperationException("this FFmpeg can't read DVDs (it needs its DVD-Video reader)");
        var title = info.Titles.FirstOrDefault(t => t.Number == o.Title) ?? throw new ArgumentException($"the disc has no title {o.Title}");
        var chapters = Selection(o, title);
        // A drive is read once (a second pass over a disc is slow); a folder or image gets exact chapter marks.
        var preindex = Directory.Exists(o.Source) || (File.Exists(o.Source) && !o.Source.StartsWith("/dev/", StringComparison.Ordinal));
        progress?.Report(new RipProgress("Reading the title", 0, 0, 0));
        var probe = new StringBuilder();
        await RipTools.RunAsync(ffprobe,
            ["-v", "error", .. InputArguments(o, preindex: false), "-show_entries", "stream=codec_type,codec_name,field_order,bits_per_raw_sample:format=duration", "-of", "json"],
            ct, line => probe.AppendLine(line)).ConfigureAwait(false);
        var (streams, duration) = ParseProbe(probe.ToString());
        var lpcm = streams.Where(s => s.Type == "audio").Select((s, i) => (s.Codec, i)).Where(x => x.Codec.StartsWith("pcm", StringComparison.Ordinal))
            .Select(x => x.i).ToList();
        var audioBits = ParseAudioBits(probe.ToString());
        var lpcmBits = lpcm.Select(k => k < audioBits.Count ? audioBits[k] : 16).ToList();
        if (duration <= 0) duration = title.Duration.TotalSeconds;
        var scan = VideoScan.Progressive;
        if (o.Video != DvdVideo.Copy)
        {
            if (!(await FfmpegEncodersAsync(ffmpeg, ct).ConfigureAwait(false)).Contains(EncoderFor(o.Video)))
                throw new InvalidOperationException($"this FFmpeg has no {EncoderFor(o.Video)}");
            progress?.Report(new RipProgress("Looking at the picture", 0, 0, 0));
            var sample = chapters is null ? SampleChapter(title.Chapters) : SampleChapter(chapters);
            scan = await ScanAsync(ffmpeg, o, sample, ParseFieldOrder(probe.ToString()), ct).ConfigureAwait(false);
        }
        var verb = o.Video switch
        {
            DvdVideo.X264 => "Encoding",
            DvdVideo.X265 => "Encoding",
            _ => "Writing",
        };
        var encoder = o.Video switch { DvdVideo.X264 => " (x264)", DvdVideo.X265 => " (x265)", _ => "" };

        var name = RipNames.Safe(o.Name ?? info.Name);
        var stem = info.MainTitle?.Number == title.Number ? name : $"{name} - Title {title.Number}";
        var titleTag = o.Name ?? info.Name;
        Directory.CreateDirectory(o.OutputRoot);

        // What gets written, run by run: (chapters or the whole title, file). Lengths weigh the progress.
        var made = new List<string>();
        string? madeFolder = null, partsFolder = null;
        try
        {
            if (chapters is null)
            {
                var output = RipNames.NewFile(o.OutputRoot, stem, ".mkv");
                made.Add(output);
                await RunMkvAsync(ffmpeg, o, preindex, lpcm, titleTag, output, scan, null, $"{verb} title {title.Number}{encoder}",
                    duration, (0, 1), o.Video, progress, ct).ConfigureAwait(false);
                return new DvdRipResult(output, [], new FileInfo(output).Length);
            }

            double Length(ChapterRun r)
            {
                var known = Enumerable.Range(r.From, r.To - r.From + 1).Select(c => title.ChapterLength(c)).ToList();
                return known.All(k => k is not null) ? known.Sum(k => k!.Value.TotalSeconds)
                    : duration * (r.To - r.From + 1) / Math.Max(1, title.Chapters);
            }

            var runs = o.ChapterFiles ? chapters.Select(c => new ChapterRun(c, c)).ToList() : Runs(chapters);
            var total = Math.Max(1e-6, runs.Sum(Length));
            double done = 0;
            (double, double) Share(ChapterRun r)
            {
                var span = (done / total, Length(r) / total);
                done += Length(r);
                return span;
            }

            if (o.ChapterFiles)
            {
                madeFolder = RipNames.NewFolder(o.OutputRoot, stem);
                Directory.CreateDirectory(madeFolder);
                long bytes = 0;
                for (var i = 0; i < runs.Count; i++)
                {
                    var c = runs[i].From;
                    var file = RipNames.NewFile(madeFolder, $"{stem} - Chapter {c:00}", ".mkv");
                    made.Add(file);
                    await RunMkvAsync(ffmpeg, o, preindex, lpcm, $"{titleTag} - Chapter {c}", file, scan, runs[i],
                        $"{verb} chapter {c} ({i + 1} of {runs.Count}){encoder}", Length(runs[i]), Share(runs[i]), o.Video, progress, ct)
                        .ConfigureAwait(false);
                    bytes += new FileInfo(file).Length;
                }
                return new DvdRipResult(madeFolder, [], bytes);
            }

            var joined = RipNames.NewFile(o.OutputRoot, $"{stem} - {ChaptersLabel(chapters)}", ".mkv");
            made.Add(joined);
            if (runs.Count == 1)
            {
                await RunMkvAsync(ffmpeg, o, preindex, lpcm, titleTag, joined, scan, runs[0],
                    $"{verb} {ChaptersLabel(chapters).ToLowerInvariant()}{encoder}", Length(runs[0]), (0, 1), o.Video, progress, ct)
                    .ConfigureAwait(false);
                return new DvdRipResult(joined, [], new FileInfo(joined).Length);
            }

            // Chapters apart: each run ripped on its own, then the parts joined as they are.
            partsFolder = RipNames.NewFolder(o.OutputRoot, ".paramaja-parts");
            Directory.CreateDirectory(partsFolder);
            var parts = new List<(ChapterRun Run, double Duration, IReadOnlyList<(double Start, double End)> Marks)>();
            var list = new StringBuilder();
            for (var i = 0; i < runs.Count; i++)
            {
                var part = Path.Combine(partsFolder, $"part{i:000}.mkv");
                var label = runs[i].From == runs[i].To ? $"chapter {runs[i].From}" : $"chapters {runs[i].From}-{runs[i].To}";
                await RunMkvAsync(ffmpeg, o, preindex, lpcm, titleTag, part, scan, runs[i],
                    $"{verb} {label} ({i + 1} of {runs.Count}){encoder}", Length(runs[i]), Share(runs[i]), o.Video, progress, ct,
                    partBits: lpcmBits).ConfigureAwait(false);
                var shown = new StringBuilder();
                await RipTools.RunAsync(ffprobe, ["-v", "error", "-show_chapters", "-show_entries", "format=duration", "-of", "json", part],
                    ct, line => shown.AppendLine(line)).ConfigureAwait(false);
                var (length, marks) = ParseParts(shown.ToString());
                parts.Add((runs[i], length, marks));
                list.Append("file '").Append(part.Replace("'", "'\\''")).Append("'\n");
            }
            progress?.Report(new RipProgress("Joining the chapters", 0, 0, 1));
            var listFile = Path.Combine(partsFolder, "parts.txt");
            var metaFile = Path.Combine(partsFolder, "chapters.txt");
            await File.WriteAllTextAsync(listFile, list.ToString(), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(metaFile, JoinedChapters(titleTag, parts), ct).ConfigureAwait(false);
            await RipTools.RunAsync(ffmpeg, JoinArguments(listFile, metaFile, joined, lpcm), ct).ConfigureAwait(false);
            return new DvdRipResult(joined, [], new FileInfo(joined).Length);
        }
        catch
        {
            foreach (var file in made)
            {
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            if (madeFolder is not null) TryDelete(madeFolder);
            throw;
        }
        finally
        {
            if (partsFolder is not null) TryDelete(partsFolder);
        }
    }

    /// <summary>
    /// Parts joined end to end (FFmpeg's concat reader), with the marks and title from the metadata
    /// file; the LPCM tracks, PCM in the parts, become FLAC here.
    /// </summary>
    internal static IEnumerable<string> JoinArguments(string list, string chapters, string output, IReadOnlyList<int> lpcm) =>
    [
        "-hide_banner", "-nostdin", "-loglevel", "error", "-nostats",
        "-f", "concat", "-safe", "0", "-i", list,
        "-i", chapters,
        "-map", "0", "-map_metadata", "1", "-map_chapters", "1", "-c", "copy",
        .. lpcm.SelectMany(k => new[] { $"-c:a:{k}", "flac", $"-compression_level:a:{k}", "8" }),
        "-f", "matroska", output,
    ];

    /// <summary>One FFmpeg run to an MKV, its progress reported as <paramref name="share"/> (start, size) of the whole rip.</summary>
    private static async Task RunMkvAsync(string ffmpeg, DvdRipOptions o, bool preindex, IReadOnlyList<int> lpcm, string titleTag, string output,
        VideoScan scan, ChapterRun? range, string stage, double seconds, (double Start, double Size) share, DvdVideo video,
        IProgress<RipProgress>? progress, CancellationToken ct, IReadOnlyList<int>? partBits = null)
    {
        double speed = 0;
        await RipTools.RunAsync(ffmpeg, MkvArguments(o, preindex, lpcm, titleTag, output, scan, range, partBits), ct, line =>
        {
            // -progress writes a block of key=value lines per update, speed= before out_time_us=.
            if (line.StartsWith("speed=", StringComparison.Ordinal)
                && double.TryParse(line.AsSpan(6).TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x))
                speed = x;
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && seconds > 0)
                progress?.Report(new RipProgress(stage, 0, 0, share.Start + share.Size * Math.Clamp(us / 1e6 / seconds, 0, 1),
                    video == DvdVideo.Copy ? 0 : speed));
        }).ConfigureAwait(false);
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
