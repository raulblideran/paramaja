using System.Buffers.Binary;
using System.Text;

namespace Paramaja.Core.Playback.Sacd;

/// <summary>An SACD's two kinds of area: the stereo mix and the multichannel (5.0 / 5.1) one.</summary>
public enum SacdAreaKind { Stereo, Multichannel }

/// <summary>A track of an area: where its audio sectors are, how long it plays (in SACD frames, 75 a second) and its text.</summary>
public sealed record SacdTrack(int Number, string? Title, string? Performer, long StartSector, long LengthSectors, int Frames)
{
    public double Seconds => Frames / (double)SacdImage.FrameRate;

    public string Label => string.IsNullOrWhiteSpace(Title) ? $"Track {Number}" : Title!;
}

/// <summary>
/// An area: its channels (2, 5 or 6), whether its frames are DST-compressed or plain DSD, and its
/// tracks.
/// </summary>
public sealed record SacdArea(SacdAreaKind Kind, int Channels, bool Dst, int SampleRate, IReadOnlyList<SacdTrack> Tracks)
{
    public string Label => Kind == SacdAreaKind.Stereo ? "Stereo" : Channels switch
    {
        6 => "Multichannel 5.1",
        5 => "Multichannel 5.0",
        var n => $"Multichannel ({n} channels)",
    };
}

/// <summary>
/// An SACD image (the ISO a disc ripper makes of its high-density layer), read as the Scarlet Book
/// lays it out (structures as in sacd-ripper's libsacd): the master table of contents at sector
/// 510 with the album's text, then for each area (stereo, multichannel) its own table of contents
/// with the track list (TRL1: sectors, TRL2: times) and the tracks' text. Every value is big-endian.
/// </summary>
public sealed class SacdImage
{
    public const int SectorSize = 2048;
    public const int FrameRate = 75;
    /// <summary>DSD64: 64 × 44.1 kHz, one bit per sample and channel. SACDs have no other rate.</summary>
    public const int DsdRate = 2_822_400;
    /// <summary>A plain (uncompressed) frame holds this many bytes for each channel: 1/75 s of DSD64.</summary>
    public const int DsdBytesPerChannel = DsdRate / 8 / FrameRate;

    private const int MasterTocSector = 510;
    private const int MasterTextSector = 511;

    public string Path { get; }
    public string? AlbumTitle { get; }
    public string? AlbumArtist { get; }
    /// <summary>The stereo area first, where the disc has one.</summary>
    public IReadOnlyList<SacdArea> Areas { get; }

    private SacdImage(string path, string? title, string? artist, IReadOnlyList<SacdArea> areas)
    {
        Path = path;
        AlbumTitle = title;
        AlbumArtist = artist;
        Areas = areas;
    }

    public SacdArea? Area(SacdAreaKind kind) => Areas.FirstOrDefault(a => a.Kind == kind);

    /// <summary>Throws <see cref="InvalidDataException"/> if it isn't an SACD image.</summary>
    public static SacdImage Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        return Read(file, path);
    }

    public static SacdImage Read(Stream image, string path = "")
    {
        var mtoc = ReadSectors(image, MasterTocSector, 1);
        if (Ascii(mtoc, 0, 8) != "SACDMTOC") throw new InvalidDataException("not an SACD image (no master table of contents)");

        var area1 = (Start: BinaryPrimitives.ReadUInt32BigEndian(mtoc.AsSpan(64)), Size: BinaryPrimitives.ReadUInt16BigEndian(mtoc.AsSpan(84)));
        var area2 = (Start: BinaryPrimitives.ReadUInt32BigEndian(mtoc.AsSpan(72)), Size: BinaryPrimitives.ReadUInt16BigEndian(mtoc.AsSpan(86)));
        var charset = mtoc[128] > 0 ? mtoc[136 + 2] : (byte)2; // the first locale's character set

        string? title = null, artist = null;
        var text = TryReadSectors(image, MasterTextSector, 1);
        if (text is not null && Ascii(text, 0, 8) == "SACDText")
        {
            title = TextAt(text, BinaryPrimitives.ReadUInt16BigEndian(text.AsSpan(16)), charset);
            artist = TextAt(text, BinaryPrimitives.ReadUInt16BigEndian(text.AsSpan(18)), charset);
            // Some discs fill in only the disc's own title and artist.
            title ??= TextAt(text, BinaryPrimitives.ReadUInt16BigEndian(text.AsSpan(32)), charset);
            artist ??= TextAt(text, BinaryPrimitives.ReadUInt16BigEndian(text.AsSpan(34)), charset);
        }

        var areas = new List<SacdArea>();
        foreach (var (start, size) in new[] { area1, area2 })
        {
            if (start == 0 || size == 0) continue;
            if (ReadArea(image, start, size) is { } area) areas.Add(area);
        }
        if (areas.Count == 0) throw new InvalidDataException("the SACD image has no audio areas");
        // Some discs write the artist as a label: "Michael Jackson:".
        artist = artist?.TrimEnd(':', ';', ' ') is { Length: > 0 } a ? a : null;
        return new SacdImage(path, title, artist, areas.OrderBy(a => a.Kind).ToList());
    }

    private static SacdArea? ReadArea(Stream image, long start, int sizeSectors)
    {
        var data = TryReadSectors(image, start, Math.Clamp(sizeSectors, 1, 96));
        if (data is null) return null;
        var id = Ascii(data, 0, 8);
        if (id is not ("TWOCHTOC" or "MULCHTOC")) return null;

        var frameFormat = data[21] & 0x0F; // 0 DST, 2 and 3 plain DSD (3 frames in 14 or 16 sectors)
        var channels = data[32];
        var loudspeakers = data[33] >> 3;
        var trackCount = data[69];
        var charset = data[80] > 0 ? data[88 + 2] : (byte)2;
        var kind = channels == 2 && loudspeakers == 0 ? SacdAreaKind.Stereo : SacdAreaKind.Multichannel;
        if (channels is < 1 or > 6 || trackCount == 0) return null;

        uint[]? starts = null, lengths = null;
        int[]? durations = null;
        int textSector = -1;
        // After the area's first sector come its lists, each marked by an 8-character id.
        for (var s = 1; s < data.Length / SectorSize; s++)
        {
            var o = s * SectorSize;
            switch (Ascii(data, o, 8))
            {
                case "SACDTRL1":
                    starts = new uint[trackCount];
                    lengths = new uint[trackCount];
                    for (var i = 0; i < trackCount; i++)
                    {
                        starts[i] = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o + 8 + 4 * i));
                        lengths[i] = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o + 8 + 1020 + 4 * i));
                    }
                    break;
                case "SACDTRL2":
                    durations = new int[trackCount];
                    for (var i = 0; i < trackCount; i++)
                    {
                        var t = o + 8 + 1020 + 4 * i; // the durations follow the 255 start times
                        durations[i] = (data[t] * 60 + data[t + 1]) * FrameRate + data[t + 2];
                    }
                    break;
                case "SACDTTxt" when textSector < 0:
                    textSector = s;
                    break;
            }
        }
        if (starts is null || lengths is null) return null;

        var tracks = new List<SacdTrack>(trackCount);
        for (var i = 0; i < trackCount; i++)
        {
            var (title, performer) = textSector < 0 ? (null, null) : TrackText(data, textSector * SectorSize, i, charset);
            var frames = durations?[i] ?? 0;
            tracks.Add(new SacdTrack(i + 1, title, performer, starts[i], lengths[i], frames));
        }
        return new SacdArea(kind, channels, frameFormat == 0, DsdRate, tracks);
    }

    /// <summary>
    /// A track's title and performer from the area's text list: an offset per track into the list,
    /// where a count of items (then 3 bytes) is followed by each item's type, a byte, and its
    /// zero-ended text, padded with zeros.
    /// </summary>
    private static (string? Title, string? Performer) TrackText(byte[] data, int list, int track, byte charset)
    {
        var position = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(list + 8 + 2 * track));
        if (position == 0 || list + position >= data.Length) return (null, null);
        var p = list + position;
        int count = data[p];
        p += 4;
        string? title = null, performer = null;
        for (var i = 0; i < count && p + 2 < data.Length; i++)
        {
            var type = data[p];
            p += 2;
            var end = Array.IndexOf(data, (byte)0, p);
            if (end < 0) break;
            var text = Decode(data.AsSpan(p, end - p), charset);
            if (type == 1) title ??= text;
            else if (type == 2) performer ??= text;
            p = end;
            while (p < data.Length && data[p] == 0) p++;
        }
        return (title, performer);
    }

    private static string? TextAt(byte[] sector, int position, byte charset)
    {
        if (position <= 0 || position >= sector.Length) return null;
        var end = Array.IndexOf(sector, (byte)0, position);
        var text = Decode(sector.AsSpan(position, (end < 0 ? sector.Length : end) - position), charset);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    static SacdImage() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The Scarlet Book's character sets: ISO 646 / 8859-1, Shift-JIS, KS C 5601, GB 2312, Big5.</summary>
    private static string? Decode(ReadOnlySpan<byte> bytes, byte charset)
    {
        if (bytes.IsEmpty) return null;
        Encoding encoding;
        try
        {
            encoding = (charset & 7) switch
            {
                3 => Encoding.GetEncoding(932),
                4 => Encoding.GetEncoding(949),
                5 => Encoding.GetEncoding(936),
                6 => Encoding.GetEncoding(950),
                _ => Encoding.Latin1,
            };
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { encoding = Encoding.Latin1; }
        // Control characters some discs have in their text become spaces (as sacd-ripper does).
        var text = new string(encoding.GetString(bytes).Select(c => c < ' ' ? ' ' : c).ToArray()).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string Ascii(byte[] data, int offset, int length) =>
        offset + length <= data.Length ? Encoding.ASCII.GetString(data, offset, length) : "";

    internal static byte[] ReadSectors(Stream image, long sector, int count) =>
        TryReadSectors(image, sector, count) ?? throw new InvalidDataException("the image ends early");

    private static byte[]? TryReadSectors(Stream image, long sector, int count)
    {
        var offset = sector * SectorSize;
        if (offset + (long)count * SectorSize > image.Length) return null;
        var buffer = new byte[count * SectorSize];
        image.Position = offset;
        image.ReadExactly(buffer);
        return buffer;
    }
}
