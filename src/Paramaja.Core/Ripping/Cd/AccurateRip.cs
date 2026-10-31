using System.Buffers.Binary;
using System.Text;

namespace Paramaja.Core.Ripping.Cd;

/// <summary>One track's checksum as one pressing of the disc has it in AccurateRip, and how many rips agreed.</summary>
public sealed record AccurateRipTrack(int Confidence, uint Crc, uint OffsetFindingCrc);

/// <summary>A pressing of the disc in AccurateRip's database: a checksum per track.</summary>
public sealed record AccurateRipPressing(IReadOnlyList<AccurateRipTrack> Tracks);

/// <summary>How a ripped track compares with AccurateRip.</summary>
/// <param name="Version">1 or 2: which of the two checksums matched; 0 when none did.</param>
/// <param name="Confidence">How many rips the matching pressing has with this checksum.</param>
/// <param name="Total">All rips of the track in the database, every pressing together.</param>
public sealed record AccurateRipResult(int Version, int Confidence, int Total, bool InDatabase)
{
    public bool Accurate => Version > 0;

    public static readonly AccurateRipResult NotInDatabase = new(0, 0, 0, false);
}

/// <summary>
/// AccurateRip: a database of the checksums of everyone's rips of each disc, so a rip can be
/// checked against rips made with other drives. A disc's entry is a small binary file (dBAR):
/// for each pressing a 13-byte header (track count, the disc's three IDs) and 9 bytes per track
/// (confidence, checksum, offset-finding checksum), all little-endian.
/// </summary>
public static class AccurateRip
{
    public static IReadOnlyList<AccurateRipPressing> Parse(ReadOnlySpan<byte> data, int tracks)
    {
        var pressings = new List<AccurateRipPressing>();
        var p = 0;
        while (p + 13 <= data.Length)
        {
            int count = data[p];
            p += 13;
            if (count == 0 || p + 9 * count > data.Length) break;
            var list = new List<AccurateRipTrack>(count);
            for (var i = 0; i < count; i++, p += 9)
                list.Add(new AccurateRipTrack(data[p], BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 1)..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 5)..])));
            if (count == tracks) pressings.Add(new AccurateRipPressing(list));
        }
        return pressings;
    }

    /// <summary>The disc's entry, or none where AccurateRip doesn't have the disc (it answers 404).</summary>
    public static async Task<IReadOnlyList<AccurateRipPressing>> FetchAsync(HttpClient http, CdToc toc, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(toc.AccurateRipUrl, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Parse(data, toc.AudioTrackCount);
    }

    /// <param name="index">The track's place among the disc's audio tracks, from 0.</param>
    public static AccurateRipResult Verify(IReadOnlyList<AccurateRipPressing> pressings, int index, uint v1, uint v2)
    {
        if (pressings.Count == 0) return AccurateRipResult.NotInDatabase;
        var total = pressings.Sum(p => p.Tracks[index].Confidence);
        // v2 first: it's the newer, better checksum, and v1 rips of the same pressing are filed apart.
        foreach (var version in new[] { 2, 1 })
        {
            var crc = version == 2 ? v2 : v1;
            var match = pressings.Select(p => p.Tracks[index]).Where(t => t.Crc == crc && t.Confidence > 0).ToList();
            if (match.Count > 0) return new AccurateRipResult(version, match.Sum(t => t.Confidence), total, true);
        }
        return new AccurateRipResult(0, 0, total, total > 0);
    }

    /// <summary>
    /// The read offset of the drive that read <paramref name="samples"/>: the shift at which the
    /// track's AccurateRip v1 checksum matches a pressing's. <paramref name="samples"/> holds the
    /// track as read with no correction, with <paramref name="margin"/> extra samples on either
    /// side. Each shift's checksum comes from the previous one in a few operations (the window's
    /// sum slides with it), so all ±margin shifts cost about one pass.
    /// A disc's pressings can differ by a shift of their own (Dangerous has one that is another
    /// one's audio 931 samples later), so a drive's offset also matches the other pressings at
    /// that offset plus their shift. What decides is how many known drives have each offset
    /// (<paramref name="drivesWithOffset"/>, from AccurateRip's drive list: +6 has nearly 2000,
    /// shifts that are a pressing's own have none), after a match of some standing (two rips or
    /// more), then the smaller shift. Null when nothing matches (or the disc only has v2
    /// checksums, which can't slide).
    /// </summary>
    public static int? DetectOffset(ReadOnlySpan<uint> samples, int margin, AccurateRipTrack[] candidates,
        Func<int, int>? drivesWithOffset = null)
    {
        var n = samples.Length - 2 * margin;
        if (n <= 0 || candidates.Length == 0) return null;
        // W(o) = Σ (i+1)·x[i+o], S(o) = Σ x[i+o] over the window; o is counted from -margin.
        uint w = 0, s = 0;
        for (var i = 0; i < n; i++)
        {
            w += (uint)(i + 1) * samples[i];
            s += samples[i];
        }
        var matches = new List<(int Offset, int Confidence)>();
        for (var o = 0; ; o++)
        {
            foreach (var c in candidates)
                if (c.Crc == w && c.Confidence > 0) matches.Add((o - margin, c.Confidence));
            if (o == 2 * margin) break;
            var incoming = samples[o + n];
            w = w - s + (uint)n * incoming;
            s = s - samples[o] + incoming;
        }
        if (matches.Count == 0) return null;
        var prior = drivesWithOffset ?? (_ => 0);
        return matches
            .OrderByDescending(m => m.Confidence >= 2)
            .ThenByDescending(m => prior(m.Offset))
            .ThenBy(m => Math.Abs(m.Offset))
            .ThenByDescending(m => m.Confidence)
            .First().Offset;
    }
}

/// <summary>
/// AccurateRip's list of drive read offsets (DriveOffsets.bin): 69-byte records, each a
/// little-endian 16-bit offset in samples and the drive's name ("VENDOR - PRODUCT", 32 bytes),
/// then submission counts.
/// </summary>
public static class DriveOffsets
{
    public const string Url = "http://www.accuraterip.com/accuraterip/DriveOffsets.bin";
    private const int Record = 69;

    public static IReadOnlyDictionary<string, int> Parse(ReadOnlySpan<byte> data)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var p = 0; p + Record <= data.Length; p += Record)
        {
            var offset = BinaryPrimitives.ReadInt16LittleEndian(data[p..]);
            var name = Encoding.Latin1.GetString(data.Slice(p + 2, 32)).Split('\0')[0];
            map.TryAdd(Key(name), offset);
        }
        return map;
    }

    /// <summary>How many of the listed drives have each offset.</summary>
    public static IReadOnlyDictionary<int, int> Counts(IReadOnlyDictionary<string, int> offsets) =>
        offsets.Values.GroupBy(o => o).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>The drive's offset by its name as it answers INQUIRY (vendor and product), if listed.</summary>
    public static int? Find(IReadOnlyDictionary<string, int> offsets, string vendor, string product) =>
        offsets.TryGetValue(Key(vendor + " - " + product), out var o) ? o : null;

    /// <summary>Names compare without their padding: "PIONEER  - DVD RW  DVR-107D" is "PIONEER-DVDRWDVR-107D".</summary>
    internal static string Key(string name) =>
        new string(name.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    /// <summary>The list, from the cache folder if it's under a month old, else from AccurateRip (then cached).</summary>
    public static async Task<IReadOnlyDictionary<string, int>> LoadAsync(HttpClient http, string cacheFile, CancellationToken ct = default)
    {
        try
        {
            var info = new FileInfo(cacheFile);
            if (info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromDays(30))
                return Parse(await File.ReadAllBytesAsync(cacheFile, ct).ConfigureAwait(false));
            var data = await http.GetByteArrayAsync(Url, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            await File.WriteAllBytesAsync(cacheFile, data, ct).ConfigureAwait(false);
            return Parse(data);
        }
        catch (HttpRequestException) when (File.Exists(cacheFile))
        {
            return Parse(await File.ReadAllBytesAsync(cacheFile, ct).ConfigureAwait(false));
        }
    }
}
