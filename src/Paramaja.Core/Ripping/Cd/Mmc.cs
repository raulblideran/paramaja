using System.Buffers.Binary;
using System.Text;

namespace Paramaja.Core.Ripping.Cd;

/// <summary>What the drive says it is (SCSI INQUIRY): AccurateRip lists read offsets by this name.</summary>
public sealed record DriveIdentity(string Vendor, string Product, string Revision)
{
    public string Name => $"{Vendor.Trim()} {Product.Trim()}".Trim();
}

/// <summary>What the drive says it can do (MMC capabilities page 2Ah).</summary>
/// <param name="AccurateStream">"CD-DA stream is accurate": it can resume an audio read exactly where it stopped.</param>
/// <param name="C2Pointers">It can flag the bytes it couldn't correct (not used: many drives get it wrong).</param>
/// <param name="BufferKb">Its read buffer (cache), which re-reads have to get past.</param>
public sealed record DriveFeatures(bool AccurateStream, bool C2Pointers, int BufferKb)
{
    public static readonly DriveFeatures Unknown = new(false, false, 0);
}

/// <summary>A sector's Q sub-channel position: which track and index the disc says it's in.</summary>
public readonly record struct SubQ(int Track, int Index);

/// <summary>A disc's CD-Text (block 0): the album's and each track's title and performer.</summary>
public sealed record CdText(string? Title, string? Performer, IReadOnlyDictionary<int, string> TrackTitles,
    IReadOnlyDictionary<int, string> TrackPerformers, string? Upc = null);

/// <summary>
/// The MMC (SCSI multimedia) commands a ripper sends a CD drive, and what their answers mean.
/// Everything here is pure: the bytes go over SG_IO on Linux and SPTI on Windows.
/// </summary>
public static class Mmc
{
    public const byte OpInquiry = 0x12, OpModeSense10 = 0x5A, OpReadToc = 0x43, OpReadSubChannel = 0x42, OpReadCd = 0xBE,
        OpTestUnitReady = 0x00;

    public static byte[] Inquiry(int length = 96) => [OpInquiry, 0, 0, 0, (byte)length, 0];

    /// <summary>MODE SENSE (10) of the CD capabilities page (2Ah).</summary>
    public static byte[] ModeSenseCapabilities(int length = 256) =>
        [OpModeSense10, 0x08 /* no block descriptors */, 0x2A, 0, 0, 0, 0, (byte)(length >> 8), (byte)length, 0];

    /// <param name="format">0: the TOC (LBA addresses), 5: CD-Text.</param>
    public static byte[] ReadToc(int format, int length = 0xFFFE) =>
        [OpReadToc, 0, (byte)format, 0, 0, 0, 0, (byte)(length >> 8), (byte)length, 0];

    /// <param name="format">2: the disc's catalogue number (MCN, the barcode), 3: a track's ISRC.</param>
    public static byte[] ReadSubChannel(int format, int track = 0, int length = 48) =>
        [OpReadSubChannel, 0, 0x40 /* SubQ */, (byte)format, 0, 0, (byte)track, (byte)(length >> 8), (byte)length, 0];

    /// <summary>
    /// READ CD of audio sectors: 2352 bytes of user data each, and with <paramref name="subQ"/> the
    /// formatted Q sub-channel (16 bytes) after each.
    /// </summary>
    public static byte[] ReadCd(int lba, int sectors, bool subQ = false) =>
    [
        OpReadCd, 0x04 /* expected sector type: CD-DA */,
        (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba,
        (byte)(sectors >> 16), (byte)(sectors >> 8), (byte)sectors,
        0x10 /* user data only */, (byte)(subQ ? 0x02 : 0x00), 0,
    ];

    public const int SubQBytes = 16;

    public static DriveIdentity ParseInquiry(ReadOnlySpan<byte> data) =>
        new(Text(data, 8, 8), Text(data, 16, 16), Text(data, 32, 4).Trim());

    private static string Text(ReadOnlySpan<byte> data, int from, int length) =>
        data.Length >= from + length ? Encoding.ASCII.GetString(data.Slice(from, length)).TrimEnd('\0') : "";

    public static DriveFeatures ParseCapabilities(ReadOnlySpan<byte> data)
    {
        // An 8-byte mode parameter header, then the page.
        if (data.Length < 8 + 14) return DriveFeatures.Unknown;
        var page = data[8..];
        if ((page[0] & 0x3F) != 0x2A) return DriveFeatures.Unknown;
        return new DriveFeatures(
            AccurateStream: (page[5] & 0x02) != 0,
            C2Pointers: (page[5] & 0x10) != 0,
            BufferKb: BinaryPrimitives.ReadUInt16BigEndian(page[12..]));
    }

    /// <summary>The TOC (format 0, LBA): a 4-byte header, then 8 bytes per track and one for the lead-out (track AAh).</summary>
    public static CdToc ParseToc(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) throw new InvalidDataException("the drive returned no TOC");
        var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(data) + 2, data.Length);
        var tracks = new List<CdTrack>();
        int? leadOut = null;
        for (var p = 4; p + 8 <= length; p += 8)
        {
            var control = data[p + 1] & 0x0F;
            var number = data[p + 2];
            var lba = BinaryPrimitives.ReadInt32BigEndian(data[(p + 4)..]);
            if (number == 0xAA) leadOut = lba;
            else if (number is >= 1 and <= 99)
                tracks.Add(new CdTrack(number, lba, IsAudio: (control & 0x04) == 0, PreEmphasis: (control & 0x01) != 0,
                    CopyPermitted: (control & 0x02) != 0));
        }
        if (tracks.Count == 0 || leadOut is null) throw new InvalidDataException("the disc's TOC lists no tracks");
        return new CdToc(tracks, leadOut.Value);
    }

    /// <summary>The MCN (13 digits) from READ SUB-CHANNEL format 2, if the disc has one.</summary>
    public static string? ParseMcn(ReadOnlySpan<byte> data) =>
        data.Length >= 9 + 13 && (data[8] & 0x80) != 0 ? Clean(Encoding.ASCII.GetString(data.Slice(9, 13)), 13) : null;

    /// <summary>A track's ISRC (12 characters) from READ SUB-CHANNEL format 3, if it has one.</summary>
    public static string? ParseIsrc(ReadOnlySpan<byte> data) =>
        data.Length >= 9 + 12 && (data[8] & 0x80) != 0 ? Clean(Encoding.ASCII.GetString(data.Slice(9, 12)), 12) : null;

    private static string? Clean(string s, int length)
    {
        s = s.TrimEnd('\0', ' ');
        // Drives answer all zeros, or junk, for discs without one.
        return s.Length == length && s.All(char.IsAsciiLetterOrDigit) && s.Any(c => c != '0') ? s : null;
    }

    /// <summary>
    /// A formatted Q sub-channel (16 bytes after a sector): control/ADR, track and index in BCD.
    /// Only mode-1 frames (ADR 1) carry a position; the others carry the MCN or an ISRC.
    /// </summary>
    public static SubQ? ParseSubQ(ReadOnlySpan<byte> q)
    {
        if (q.Length < 10 || (q[0] & 0x0F) != 1) return null;
        int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);
        if ((q[1] >> 4) > 9 || (q[1] & 0x0F) > 9 || (q[2] >> 4) > 9 || (q[2] & 0x0F) > 9) return null;
        return new SubQ(Bcd(q[1]), Bcd(q[2]));
    }

    /// <summary>
    /// CD-Text (READ TOC format 5): 18-byte packs (type, track, sequence, block and character
    /// position, 12 bytes of text, CRC). Each type's text runs on across packs as nul-terminated
    /// strings, one per track starting at the pack's track number; a tab repeats the previous one.
    /// Only block 0 (the first language), ISO 8859-1 or ASCII, is read.
    /// </summary>
    public static CdText? ParseCdText(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 + 18) return null;
        var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(data) + 2, data.Length);
        var texts = new Dictionary<int, Dictionary<int, string>>(); // type → track → text
        var pending = new Dictionary<int, (int Track, List<byte> Bytes)>();
        string? previous = null;
        for (var p = 4; p + 18 <= length; p += 18)
        {
            var pack = data.Slice(p, 18);
            if (!PackCrcOk(pack)) continue;
            int type = pack[0];
            var block = (pack[3] >> 4) & 0x07;
            var doubleByte = (pack[3] & 0x80) != 0;
            if (block != 0 || doubleByte || type is < 0x80 or > 0x8E or 0x88 or 0x89 or 0x8A or 0x8B or 0x8C or 0x8D) continue;
            if (!pending.TryGetValue(type, out var current)) current = (pack[1] & 0x7F, []);
            for (var i = 4; i < 16; i++)
            {
                if (pack[i] != 0) { current.Bytes.Add(pack[i]); continue; }
                var text = Encoding.Latin1.GetString(current.Bytes.ToArray());
                if (text == "\t") text = previous ?? "";
                previous = text;
                if (!texts.TryGetValue(type, out var byTrack)) texts[type] = byTrack = [];
                if (text.Length > 0) byTrack.TryAdd(current.Track, text);
                current = (current.Track + 1, []);
            }
            pending[type] = current;
        }
        if (texts.Count == 0) return null;
        IReadOnlyDictionary<int, string> Tracks(int type) =>
            texts.TryGetValue(type, out var t) ? t.Where(kv => kv.Key > 0).ToDictionary(kv => kv.Key, kv => kv.Value) : new Dictionary<int, string>();
        string? Disc(int type) => texts.TryGetValue(type, out var t) && t.TryGetValue(0, out var v) ? v : null;
        return new CdText(Disc(0x80), Disc(0x81), Tracks(0x80), Tracks(0x81), Disc(0x8E));
    }

    /// <summary>A CD-Text pack's CRC: CCITT (x^16 + x^12 + x^5 + 1) over its 16 bytes, stored inverted.</summary>
    internal static bool PackCrcOk(ReadOnlySpan<byte> pack)
    {
        ushort crc = 0;
        for (var i = 0; i < 16; i++)
        {
            crc ^= (ushort)(pack[i] << 8);
            for (var b = 0; b < 8; b++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        var stored = (ushort)~BinaryPrimitives.ReadUInt16BigEndian(pack[16..]);
        // Some drives hand the packs over with the CRC zeroed.
        return crc == stored || BinaryPrimitives.ReadUInt16BigEndian(pack[16..]) == 0;
    }

    /// <summary>Makes a pack's CRC (for tests and images).</summary>
    internal static void SetPackCrc(Span<byte> pack)
    {
        ushort crc = 0;
        for (var i = 0; i < 16; i++)
        {
            crc ^= (ushort)(pack[i] << 8);
            for (var b = 0; b < 8; b++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        BinaryPrimitives.WriteUInt16BigEndian(pack[16..], (ushort)~crc);
    }
}
