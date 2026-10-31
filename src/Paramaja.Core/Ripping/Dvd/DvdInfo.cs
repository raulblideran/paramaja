using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Paramaja.Core.Input;

namespace Paramaja.Core.Ripping.Dvd;

/// <summary>An audio stream as a title set's IFO describes it.</summary>
public sealed record DvdAudio(string Codec, int Channels, string? Language)
{
    public string Label => $"{Codec} {(Channels == 6 ? "5.1" : Channels == 2 ? "stereo" : Channels == 1 ? "mono" : $"{Channels}ch")}"
                           + (Language is null ? "" : $" [{Language}]");
}

/// <summary>A title of the DVD: the main film, an extra, a menu loop…</summary>
/// <param name="TitleSet">The VTS it's in (VTS_nn).</param>
public sealed record DvdTitle(int Number, int TitleSet, int Chapters, int Angles, TimeSpan Duration,
    IReadOnlyList<DvdAudio> Audio, IReadOnlyList<string?> Subtitles)
{
    public string Label
    {
        get
        {
            var parts = new List<string>
            {
                $"Title {Number}", TimeFormatted(Duration), Chapters == 1 ? "1 chapter" : $"{Chapters} chapters",
            };
            if (Audio.Count > 0) parts.Add(string.Join(", ", Audio.Select(a => a.Label)));
            if (Subtitles.Count > 0) parts.Add(Subtitles.Count == 1 ? "1 subtitle" : $"{Subtitles.Count} subtitles");
            if (Angles > 1) parts.Add($"{Angles} angles");
            return string.Join(" · ", parts);
        }
    }

    private static string TimeFormatted(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// A DVD-Video's titles and files, from its IFOs (never encrypted, so they read as plain files):
/// VIDEO_TS.IFO's title table says where each title is; each title set's IFO gives its streams and
/// its program chains, whose playback times are the titles' lengths. The disc may be a folder, an
/// image (.iso) or a drive (read through ISO 9660, like an image).
/// </summary>
public sealed class DvdInfo
{
    private const int Sector = 2048;

    public required string Name { get; init; }
    public required IReadOnlyList<DvdTitle> Titles { get; init; }
    /// <summary>The VIDEO_TS folder's files and their sizes, as the disc lists them.</summary>
    public required IReadOnlyList<(string Name, long Length)> Files { get; init; }

    /// <summary>The main title: the longest.</summary>
    public DvdTitle? MainTitle => Titles.OrderByDescending(t => t.Duration).FirstOrDefault();

    /// <summary>Reads the disc. Throws <see cref="InvalidDataException"/> if it isn't a DVD-Video.</summary>
    public static DvdInfo Read(string source)
    {
        if (Directory.Exists(source) && VideoTs(source) is { } folder)
        {
            var files = Directory.EnumerateFiles(folder).Select(f => (Path.GetFileName(f).ToUpperInvariant(), new FileInfo(f).Length)).ToList();
            byte[]? File(string name) =>
                Directory.EnumerateFiles(folder).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) is { } path
                    ? ReadStart(path)
                    : null;
            return new DvdInfo { Name = DiscName(source, null), Titles = ParseTitles(File), Files = files };
        }
        using var image = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        var listing = DiscImage.VideoTsFiles(image);
        if (listing.Count == 0) throw new InvalidDataException("no VIDEO_TS folder on this disc");
        var titles = ParseTitles(name =>
            DiscImage.IsoEntry(image, DiscImage.IsoRoot(image), "VIDEO_TS", directory: true) is { } videoTs
            && DiscImage.IsoEntry(image, videoTs, name, directory: false) is { } entry
                ? DiscImage.ReadExtent(image, entry, IfoMax)
                : null);
        return new DvdInfo { Name = DiscName(source, DiscImage.VolumeLabel(image)), Titles = titles, Files = listing };
    }

    /// <summary>IFOs are small; a title set's with many program chains still fits in 4 MB.</summary>
    private const int IfoMax = 4 << 20;

    private static byte[] ReadStart(string path)
    {
        using var f = System.IO.File.OpenRead(path);
        var bytes = new byte[Math.Min(f.Length, IfoMax)];
        f.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>A folder's VIDEO_TS: the folder itself or its child of that name.</summary>
    internal static string? VideoTs(string folder)
    {
        if (Path.GetFileName(folder.TrimEnd('/', '\\')).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase)) return folder;
        return Directory.EnumerateDirectories(folder).FirstOrDefault(d => Path.GetFileName(d).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What to call the rip: the volume label ("THE_ONE" reads "The One"), else the folder's or
    /// image's name; a drive's raw path or a VIDEO_TS folder gives way to the one above.
    /// </summary>
    internal static string DiscName(string source, string? label)
    {
        if (label is { Length: > 0 } && !label.Equals("DVD_VIDEO", StringComparison.OrdinalIgnoreCase))
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(label.Replace('_', ' ').ToLowerInvariant());
        var trimmed = source.TrimEnd('/', '\\');
        if (Path.GetFileName(trimmed).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase)) trimmed = Path.GetDirectoryName(trimmed) ?? trimmed;
        if (System.IO.File.Exists(trimmed) && !trimmed.StartsWith("/dev/", StringComparison.Ordinal)) return Path.GetFileNameWithoutExtension(trimmed);
        return Path.GetFileName(trimmed) is { Length: > 0 } name && !trimmed.StartsWith("/dev/", StringComparison.Ordinal) ? name : "DVD";
    }

    /// <summary>
    /// The titles, from VIDEO_TS.IFO's TT_SRPT (12 bytes each: angles at 1, chapters at 2, title set
    /// at 6, its title number within the set at 7) and each set's IFO: the chapter table (VTS_PTT_SRPT)
    /// names each title's program chains, whose playback times (BCD) add up to its length.
    /// </summary>
    internal static IReadOnlyList<DvdTitle> ParseTitles(Func<string, byte[]?> file)
    {
        if (file("VIDEO_TS.IFO") is not { Length: >= Sector } vmg || Encoding.ASCII.GetString(vmg, 0, 12) != "DVDVIDEO-VMG")
            throw new InvalidDataException("VIDEO_TS.IFO is missing or isn't a DVD-Video's");
        var table = (int)BinaryPrimitives.ReadUInt32BigEndian(vmg.AsSpan(0xC4)) * Sector;
        if (table <= 0 || table + 8 > vmg.Length) return [];
        var count = BinaryPrimitives.ReadUInt16BigEndian(vmg.AsSpan(table));
        var sets = new Dictionary<int, byte[]?>();
        var titles = new List<DvdTitle>();
        for (var t = 0; t < count && table + 8 + 12 * (t + 1) <= vmg.Length; t++)
        {
            var e = vmg.AsSpan(table + 8 + 12 * t, 12);
            int angles = e[1], chapters = BinaryPrimitives.ReadUInt16BigEndian(e[2..]), set = e[6], inSet = e[7];
            if (!sets.TryGetValue(set, out var vts))
                sets[set] = vts = file($"VTS_{set:00}_0.IFO") is { Length: >= Sector } v && Encoding.ASCII.GetString(v, 0, 12) == "DVDVIDEO-VTS" ? v : null;
            if (vts is null) continue;
            titles.Add(new DvdTitle(t + 1, set, chapters, Math.Max(1, angles), Duration(vts, inSet), Audio(vts), Subtitles(vts)));
        }
        return titles;
    }

    private static TimeSpan Duration(byte[] vts, int titleInSet)
    {
        var ptt = (int)BinaryPrimitives.ReadUInt32BigEndian(vts.AsSpan(0xC8)) * Sector;
        var pgci = (int)BinaryPrimitives.ReadUInt32BigEndian(vts.AsSpan(0xCC)) * Sector;
        if (ptt <= 0 || pgci <= 0 || ptt + 8 + 4 * titleInSet > vts.Length || pgci + 8 > vts.Length) return TimeSpan.Zero;
        // The title's chapters: 4-byte entries (program chain, program) from its offset to the next title's (or the table's end).
        var titlesInTable = BinaryPrimitives.ReadUInt16BigEndian(vts.AsSpan(ptt));
        var tableEnd = (int)BinaryPrimitives.ReadUInt32BigEndian(vts.AsSpan(ptt + 4)) + 1;
        var from = (int)BinaryPrimitives.ReadUInt32BigEndian(vts.AsSpan(ptt + 8 + 4 * (titleInSet - 1)));
        var to = titleInSet < titlesInTable ? (int)BinaryPrimitives.ReadUInt32BigEndian(vts.AsSpan(ptt + 8 + 4 * titleInSet)) : tableEnd;
        var chains = new SortedSet<int>();
        for (var p = ptt + from; p + 4 <= ptt + to && p + 4 <= vts.Length; p += 4) chains.Add(BinaryPrimitives.ReadUInt16BigEndian(vts.AsSpan(p)));
        var pgcCount = BinaryPrimitives.ReadUInt16BigEndian(vts.AsSpan(pgci));
        var total = TimeSpan.Zero;
        foreach (var chain in chains)
        {
            if (chain < 1 || chain > pgcCount || pgci + 8 + 8 * chain > vts.Length) continue;
            var at = pgci + (int)BinaryPrimitives.ReadUInt32BigEndian(vts.AsSpan(pgci + 8 + 8 * (chain - 1) + 4));
            if (at + 8 <= vts.Length) total += PlaybackTime(vts.AsSpan(at + 4, 4));
        }
        return total;
    }

    /// <summary>A program chain's playback time: hours, minutes, seconds in BCD, then frames (BCD, the top two bits the rate: 1 is 25, 3 is 30).</summary>
    internal static TimeSpan PlaybackTime(ReadOnlySpan<byte> t)
    {
        static int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);
        var rate = (t[3] >> 6) == 3 ? 30.0 : 25.0;
        return TimeSpan.FromSeconds(Bcd(t[0]) * 3600 + Bcd(t[1]) * 60 + Bcd(t[2]) + Bcd((byte)(t[3] & 0x3F)) / rate);
    }

    /// <summary>The set's audio streams: their count at 0x202, then 8 bytes each (coding in bits 7-5, channels − 1 in bits 2-0 of byte 1, language at 2).</summary>
    private static IReadOnlyList<DvdAudio> Audio(byte[] vts)
    {
        var list = new List<DvdAudio>();
        var n = Math.Min((int)BinaryPrimitives.ReadUInt16BigEndian(vts.AsSpan(0x202)), 8);
        for (var i = 0; i < n; i++)
        {
            var a = vts.AsSpan(0x204 + 8 * i, 8);
            var codec = (a[0] >> 5) switch { 0 => "AC-3", 2 => "MPEG", 3 => "MPEG", 4 => "LPCM", 6 => "DTS", _ => "audio" };
            list.Add(new DvdAudio(codec, (a[1] & 0x07) + 1, Language(a[0] & 0x0C, a[2..4])));
        }
        return list;
    }

    /// <summary>The set's subtitle streams: their count at 0x254, then 6 bytes each, the language at 2.</summary>
    private static IReadOnlyList<string?> Subtitles(byte[] vts)
    {
        var n = Math.Min((int)BinaryPrimitives.ReadUInt16BigEndian(vts.AsSpan(0x254)), 32);
        return Enumerable.Range(0, n).Select(i => Language(vts[0x256 + 6 * i] & 0x03, vts.AsSpan(0x256 + 6 * i + 2, 2))).ToList();
    }

    private static string? Language(int typeBits, ReadOnlySpan<byte> code) =>
        typeBits != 0 && char.IsAsciiLetter((char)code[0]) && char.IsAsciiLetter((char)code[1])
            ? Encoding.ASCII.GetString(code).ToLowerInvariant()
            : null;
}
