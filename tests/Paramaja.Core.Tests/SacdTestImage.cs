using System.Buffers.Binary;
using System.Text;

namespace Paramaja.Core.Tests;

/// <summary>
/// Writes small SACD images the way the Scarlet Book lays them out (master TOC at sector 510,
/// area TOCs with TRL1 / TRL2 / track text, audio sectors of packets), for the reader to be tested
/// against without a real disc. Each frame's bytes are a pattern tests can check.
/// </summary>
public static class SacdTestImage
{
    public const int Sector = 2048;
    public const int DsdFrame = 4704; // bytes per channel in 1/75 s of DSD64

    public sealed record Track(string Title, int Frames);

    /// <summary>An area: its channels, plain DSD or DST (DST frames then have <paramref name="DstSizes"/> bytes in turn).</summary>
    public sealed record Area(bool Stereo, int Channels, Track[] Tracks, bool Dst = false, int[]? DstSizes = null);

    /// <summary>A frame's bytes: different for every area, track and frame.</summary>
    public static byte[] FrameBytes(int area, int track, int frame, int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(area * 101 + track * 31 + frame * 7 + i * 13);
        return bytes;
    }

    public static int FrameSize(Area area, int globalFrame) =>
        area.Dst ? area.DstSizes![globalFrame % area.DstSizes.Length] : area.Channels * DsdFrame;

    public static string Write(string path, string? album, string? artist, params Area[] areas)
    {
        var sectors = new List<byte[]>();
        for (var i = 0; i < 512; i++) sectors.Add(new byte[Sector]); // up to the master TOC and its text

        var tocStarts = new List<int>();
        var areaTocSize = 4;
        foreach (var _ in areas)
        {
            tocStarts.Add(sectors.Count);
            for (var i = 0; i < areaTocSize; i++) sectors.Add(new byte[Sector]);
        }

        // The audio, area by area: frames packed into sectors without gaps between tracks.
        for (var a = 0; a < areas.Length; a++)
        {
            var area = areas[a];
            var frames = new List<(int Track, int Frame, byte[] Bytes)>();
            var global = 0;
            for (var t = 0; t < area.Tracks.Length; t++)
                for (var f = 0; f < area.Tracks[t].Frames; f++, global++)
                    frames.Add((t, f, FrameBytes(a, t, f, FrameSize(area, global))));
            var first = sectors.Count;
            var (audio, trackFirstSector) = Pack(frames, area.Dst, area.Tracks.Length);
            sectors.AddRange(audio);
            WriteAreaToc(sectors, tocStarts[a], areaTocSize, area, first, first + audio.Count - 1,
                trackFirstSector.Select(s => first + s).ToArray(), audio.Count);
        }

        WriteMasterToc(sectors[510], areas, tocStarts, areaTocSize);
        WriteMasterText(sectors[511], album, artist);

        using var file = File.Create(path);
        foreach (var s in sectors) file.Write(s);
        return path;
    }

    /// <summary>
    /// The sectors for the frames: each one's header (packet and frame-start counts, the DST flag),
    /// its packets' infos (frame start, type, length), its frame starts' infos (time code, and for
    /// DST the frame's packet count), the packets, and padding.
    /// </summary>
    private static (List<byte[]> Sectors, int[] TrackFirstSector) Pack(List<(int Track, int Frame, byte[] Bytes)> frames, bool dst, int tracks)
    {
        // Lay the packets out first: a DST frame's info needs to know how many packets it takes.
        var layout = new List<List<(int Frame, int Offset, int Length, bool Start)>>();
        var infoSize = dst ? 4 : 3;
        int frame = 0, offset = 0;
        while (frame < frames.Count)
        {
            var packets = new List<(int, int, int, bool)>();
            int starts = 0, payload = 0;
            while (frame < frames.Count && packets.Count < 7)
            {
                var start = offset == 0;
                var overhead = 1 + 2 * (packets.Count + 1) + infoSize * (starts + (start ? 1 : 0));
                var space = Sector - overhead - payload;
                if (space <= (start ? 8 : 0)) break;
                var take = Math.Min(Math.Min(frames[frame].Bytes.Length - offset, space), 2045);
                packets.Add((frame, offset, take, start));
                if (start) starts++;
                payload += take;
                offset += take;
                if (offset == frames[frame].Bytes.Length) { frame++; offset = 0; }
            }
            layout.Add(packets);
        }
        var packetsOf = new int[frames.Count];
        foreach (var sector in layout) foreach (var p in sector) packetsOf[p.Frame]++;

        var trackFirst = new int[tracks];
        Array.Fill(trackFirst, -1);
        var result = new List<byte[]>();
        foreach (var packets in layout)
        {
            var s = new byte[Sector];
            var starts = packets.Where(p => p.Start).ToList();
            s[0] = (byte)(packets.Count << 5 | starts.Count << 2 | (dst ? 1 : 0));
            var pos = 1;
            foreach (var (_, _, length, start) in packets)
            {
                s[pos++] = (byte)((start ? 0x80 : 0) | 2 << 3 | length >> 8);
                s[pos++] = (byte)length;
            }
            foreach (var p in starts)
            {
                var t = p.Frame; // the time code: the frame's place in the area
                s[pos++] = (byte)(t / 75 / 60);
                s[pos++] = (byte)(t / 75 % 60);
                s[pos++] = (byte)(t % 75);
                if (dst) s[pos++] = (byte)(packetsOf[p.Frame] << 2);
                var track = frames[p.Frame].Track;
                if (trackFirst[track] < 0) trackFirst[track] = result.Count;
            }
            foreach (var (f, off, length, _) in packets)
            {
                frames[f].Bytes.AsSpan(off, length).CopyTo(s.AsSpan(pos));
                pos += length;
            }
            result.Add(s);
        }
        return (result, trackFirst);
    }

    private static void WriteAreaToc(List<byte[]> sectors, int start, int size, Area area, int trackStart, int trackEnd,
        int[] trackSectors, int audioSectors)
    {
        var toc = sectors[start];
        Encoding.ASCII.GetBytes(area.Stereo ? "TWOCHTOC" : "MULCHTOC").CopyTo(toc, 0);
        toc[8] = 1;
        toc[9] = 20;
        BinaryPrimitives.WriteUInt16BigEndian(toc.AsSpan(10), (ushort)size);
        toc[20] = 4; // 64 × 44.1 kHz
        toc[21] = (byte)(area.Dst ? 0 : 2);
        toc[32] = (byte)area.Channels;
        toc[33] = (byte)((area.Stereo ? 0 : area.Channels == 6 ? 4 : 3) << 3);
        toc[69] = (byte)area.Tracks.Length;
        BinaryPrimitives.WriteUInt32BigEndian(toc.AsSpan(72), (uint)trackStart);
        BinaryPrimitives.WriteUInt32BigEndian(toc.AsSpan(76), (uint)trackEnd);
        toc[80] = 1;
        toc[88] = (byte)'e';
        toc[89] = (byte)'n';
        toc[90] = 2; // ISO 8859-1

        var trl1 = sectors[start + 1];
        Encoding.ASCII.GetBytes("SACDTRL1").CopyTo(trl1, 0);
        var trl2 = sectors[start + 2];
        Encoding.ASCII.GetBytes("SACDTRL2").CopyTo(trl2, 0);
        var text = sectors[start + 3];
        Encoding.ASCII.GetBytes("SACDTTxt").CopyTo(text, 0);
        var textPos = 8 + 2 * 255;
        var elapsed = 0;
        for (var t = 0; t < area.Tracks.Length; t++)
        {
            var next = t + 1 < area.Tracks.Length ? trackSectors[t + 1] : trackStart + audioSectors;
            BinaryPrimitives.WriteUInt32BigEndian(trl1.AsSpan(8 + 4 * t), (uint)trackSectors[t]);
            BinaryPrimitives.WriteUInt32BigEndian(trl1.AsSpan(8 + 1020 + 4 * t), (uint)(next - trackSectors[t]));
            Time(trl2.AsSpan(8 + 4 * t), elapsed);
            Time(trl2.AsSpan(8 + 1020 + 4 * t), area.Tracks[t].Frames);
            elapsed += area.Tracks[t].Frames;

            BinaryPrimitives.WriteUInt16BigEndian(text.AsSpan(8 + 2 * t), (ushort)textPos);
            text[textPos] = 1;      // one item
            textPos += 4;
            text[textPos++] = 1;    // the title
            text[textPos++] = 0x20;
            var bytes = Encoding.Latin1.GetBytes(area.Tracks[t].Title);
            bytes.CopyTo(text, textPos);
            textPos += bytes.Length + 1;
            while (textPos % 4 != 0) textPos++;
        }
    }

    private static void Time(Span<byte> at, int frames)
    {
        at[0] = (byte)(frames / 75 / 60);
        at[1] = (byte)(frames / 75 % 60);
        at[2] = (byte)(frames % 75);
    }

    private static void WriteMasterToc(byte[] mtoc, Area[] areas, List<int> tocStarts, int size)
    {
        Encoding.ASCII.GetBytes("SACDMTOC").CopyTo(mtoc, 0);
        mtoc[8] = 1;
        mtoc[9] = 20;
        for (var a = 0; a < areas.Length; a++)
        {
            var at = areas[a].Stereo ? 64 : 72;
            BinaryPrimitives.WriteUInt32BigEndian(mtoc.AsSpan(at), (uint)tocStarts[a]);
            BinaryPrimitives.WriteUInt32BigEndian(mtoc.AsSpan(at + 4), (uint)tocStarts[a]);
            BinaryPrimitives.WriteUInt16BigEndian(mtoc.AsSpan(areas[a].Stereo ? 84 : 86), (ushort)size);
        }
        mtoc[128] = 1;
        mtoc[136] = (byte)'e';
        mtoc[137] = (byte)'n';
        mtoc[138] = 2;
    }

    private static void WriteMasterText(byte[] text, string? album, string? artist)
    {
        Encoding.ASCII.GetBytes("SACDText").CopyTo(text, 0);
        if (album is not null)
        {
            BinaryPrimitives.WriteUInt16BigEndian(text.AsSpan(16), 64);
            Encoding.Latin1.GetBytes(album).CopyTo(text, 64);
        }
        if (artist is not null)
        {
            BinaryPrimitives.WriteUInt16BigEndian(text.AsSpan(18), 256);
            Encoding.Latin1.GetBytes(artist).CopyTo(text, 256);
        }
    }
}
