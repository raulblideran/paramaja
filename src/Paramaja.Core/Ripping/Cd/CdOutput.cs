using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Paramaja.Core.Ripping.Cd;

public enum CdRipFormat { Flac, Wav }

/// <summary>WAV files: 44.1 kHz, 16-bit stereo, with their tags in a RIFF LIST/INFO chunk (never ID3).</summary>
public static class WavFile
{
    public const int HeaderBytes = 44;

    /// <summary>The 44-byte header, for <paramref name="dataBytes"/> of audio.</summary>
    public static byte[] Header(long dataBytes, int listBytes = 0)
    {
        var h = new byte[HeaderBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)(36 + dataBytes + listBytes));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(h, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1);       // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), 2);       // stereo
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), 44_100);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), 44_100 * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(h, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)dataBytes);
        return h;
    }

    /// <summary>A LIST/INFO chunk: title (INAM), artist (IART), album (IPRD), track (ITRK), date (ICRD), genre (IGNR), in UTF-8.</summary>
    public static byte[] InfoChunk(IEnumerable<(string Id, string? Value)> tags)
    {
        using var body = new MemoryStream();
        body.Write("INFO"u8);
        var size = new byte[4];
        foreach (var (id, value) in tags)
        {
            if (string.IsNullOrEmpty(value)) continue;
            var text = Encoding.UTF8.GetBytes(value + "\0");
            body.Write(Encoding.ASCII.GetBytes(id));
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)text.Length);
            body.Write(size);
            body.Write(text);
            if (text.Length % 2 == 1) body.WriteByte(0);
        }
        var chunk = new byte[8 + body.Length];
        Encoding.ASCII.GetBytes("LIST").CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)body.Length);
        body.ToArray().CopyTo(chunk, 8);
        return chunk;
    }

    public static IEnumerable<(string, string?)> InfoTags(AlbumTags album, TrackTags track) =>
    [
        ("INAM", track.Title), ("IART", track.Artist), ("IPRD", album.Album),
        ("ITRK", track.Number.ToString(CultureInfo.InvariantCulture)), ("ICRD", album.Date), ("IGNR", album.Genre),
    ];

    /// <summary>Ends a WAV whose audio follows a placeholder header: appends the tags, then writes the real header.</summary>
    public static void Finish(Stream wav, long dataBytes, byte[]? info)
    {
        wav.Seek(0, SeekOrigin.End);
        if (info is not null) wav.Write(info);
        wav.Seek(0, SeekOrigin.Begin);
        wav.Write(Header(dataBytes, info?.Length ?? 0));
        wav.Flush();
    }
}

/// <summary>FLAC with the reference encoder (libFLAC's flac), compression level 8, verified as it encodes.</summary>
public static class FlacEncoder
{
    public static string? Find(string? appDirectory = null) => RipTools.Find("flac", appDirectory);

    /// <summary>Vorbis comments as rippers and taggers (Picard, foobar2000) write them.</summary>
    public static IEnumerable<(string Name, string? Value)> Tags(AlbumTags album, TrackTags track, int trackTotal) =>
    [
        ("TITLE", track.Title), ("ARTIST", track.Artist), ("ALBUM", album.Album), ("ALBUMARTIST", album.AlbumArtist),
        ("DATE", album.Date), ("GENRE", album.Genre),
        ("TRACKNUMBER", track.Number.ToString(CultureInfo.InvariantCulture)),
        ("TRACKTOTAL", trackTotal.ToString(CultureInfo.InvariantCulture)),
        ("DISCNUMBER", album.DiscNumber.ToString(CultureInfo.InvariantCulture)),
        ("DISCTOTAL", album.DiscTotal.ToString(CultureInfo.InvariantCulture)),
        ("ISRC", track.Isrc), ("LABEL", album.Label), ("CATALOGNUMBER", album.CatalogNumber), ("BARCODE", album.Barcode),
        ("RELEASECOUNTRY", album.Country),
        ("MUSICBRAINZ_ALBUMID", album.ReleaseId), ("MUSICBRAINZ_RELEASEGROUPID", album.ReleaseGroupId),
        ("MUSICBRAINZ_ALBUMARTISTID", album.AlbumArtistId), ("MUSICBRAINZ_TRACKID", track.RecordingId),
        ("MUSICBRAINZ_RELEASETRACKID", track.TrackId), ("MUSICBRAINZ_DISCID", album.DiscId),
    ];

    public static IEnumerable<string> Arguments(string wav, string flac, IEnumerable<(string Name, string? Value)> tags, string? cover)
    {
        yield return "-8";
        yield return "--verify";
        yield return "--silent";
        yield return "--force";
        foreach (var (name, value) in tags)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            yield return "-T";
            yield return $"{name}={value}";
        }
        if (cover is not null) yield return "--picture=" + cover; // type 3, the front cover
        yield return "-o";
        yield return flac;
        yield return wav;
    }

    public static Task EncodeAsync(string exe, string wav, string flac, IEnumerable<(string, string?)> tags, string? cover, CancellationToken ct) =>
        RipTools.RunAsync(exe, Arguments(wav, flac, tags, cover), ct);
}

/// <summary>
/// The rip's cue sheet, as EAC writes one for a file per track with the gaps appended to the
/// previous track: each track's INDEX 01 starts its file, and its pre-gap (INDEX 00) is the end of
/// the file before.
/// </summary>
public static class CueWriter
{
    public sealed record Entry(TrackTags Tags, string FileName, int Sectors, int Pregap, bool PreEmphasis);

    public static string Write(AlbumTags album, IReadOnlyList<Entry> tracks, string? mcn, uint freedbId, string comment)
    {
        var b = new StringBuilder();
        void Line(string s) => b.Append(s).Append("\r\n");
        string Q(string s) => "\"" + s.Replace("\"", "'") + "\"";
        if (album.Genre is { } genre) Line($"REM GENRE {Q(genre)}");
        if (album.Year is { } year) Line($"REM DATE {year}");
        Line(string.Create(CultureInfo.InvariantCulture, $"REM DISCID {freedbId:X8}"));
        Line($"REM COMMENT {Q(comment)}");
        if ((mcn ?? album.Barcode) is { } catalog && catalog.All(char.IsAsciiDigit) && catalog.Length is 12 or 13) Line($"CATALOG {catalog.PadLeft(13, '0')}");
        Line($"PERFORMER {Q(album.AlbumArtist)}");
        Line($"TITLE {Q(album.Album)}");
        for (var i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            // A track with a gap starts in the file before (INDEX 00), then its own file begins.
            var gap = i > 0 && t.Pregap > 0;
            if (!gap) Line($"FILE {Q(t.FileName)} WAVE");
            Line(string.Create(CultureInfo.InvariantCulture, $"  TRACK {t.Tags.Number:00} AUDIO"));
            Line($"    TITLE {Q(t.Tags.Title)}");
            Line($"    PERFORMER {Q(t.Tags.Artist)}");
            if (t.Tags.Isrc is { Length: 12 } isrc) Line($"    ISRC {isrc}");
            if (t.PreEmphasis) Line("    FLAGS PRE");
            if (gap)
            {
                Line($"    INDEX 00 {CdToc.CueTime(tracks[i - 1].Sectors - t.Pregap)}");
                Line($"FILE {Q(t.FileName)} WAVE");
            }
            Line("    INDEX 01 00:00:00");
        }
        return b.ToString();
    }
}
