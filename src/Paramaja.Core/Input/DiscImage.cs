using System.Buffers.Binary;
using System.Text;

namespace Paramaja.Core.Input;

/// <summary>What a disc image (.iso) holds.</summary>
public enum DiscImageKind { Unknown, Dvd, Bluray, Sacd }

/// <summary>
/// Tells a DVD, Blu-ray or SACD image apart from any other .iso by reading the few sectors that
/// say so, without mounting it:
/// <list type="bullet">
/// <item>an SACD's master table of contents ("SACDMTOC") sits at sector 510;</item>
/// <item>DVD-Video and most Blu-ray images carry an ISO 9660 file system whose root holds VIDEO_TS
/// or BDMV;</item>
/// <item>Blu-rays without one are UDF 2.50 or later (DVDs are UDF 1.02), which the UDF logical
/// volume descriptor's domain identifier says.</item>
/// </list>
/// </summary>
public static class DiscImage
{
    public const int SectorSize = 2048;
    private const int SacdMasterTocSector = 510;
    private const int IsoPrimaryVolumeSector = 16;
    private const int UdfAnchorSector = 256;

    public static DiscImageKind Probe(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            return Probe(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DiscImageKind.Unknown; }
    }

    public static DiscImageKind Probe(Stream image)
    {
        if (Sector(image, SacdMasterTocSector) is { } mtoc && Encoding.ASCII.GetString(mtoc, 0, 8) == "SACDMTOC")
            return DiscImageKind.Sacd;
        var udfRevision = UdfRevision(image);
        switch (IsoRootFolders(image))
        {
            case { } names when names.Contains("VIDEO_TS"): return DiscImageKind.Dvd;
            case { } names when names.Contains("BDMV"): return DiscImageKind.Bluray;
            case not null: return udfRevision >= 0x0250 ? DiscImageKind.Bluray : DiscImageKind.Unknown;
        }
        // UDF alone: a Blu-ray from 2.50 up, else most likely a DVD authored without the ISO 9660 bridge.
        return udfRevision switch
        {
            >= 0x0250 => DiscImageKind.Bluray,
            > 0 => DiscImageKind.Dvd,
            _ => DiscImageKind.Unknown,
        };
    }

    private static byte[]? Sector(Stream image, long sector, int count = 1)
    {
        var offset = sector * SectorSize;
        if (offset + (long)count * SectorSize > image.Length) return null;
        var buffer = new byte[count * SectorSize];
        image.Position = offset;
        image.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>The ISO 9660 root folder's entries (upper case, without ";1"); null if there's no ISO 9660 file system.</summary>
    internal static HashSet<string>? IsoRootFolders(Stream image)
    {
        if (IsoRoot(image) is not { } root) return null;
        return Entries(image, root).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Where a file or folder's data is: its first sector and its length.</summary>
    internal readonly record struct Extent(long Sector, long Length);

    /// <summary>The root folder (from the primary volume descriptor's root directory record); null without ISO 9660.</summary>
    internal static Extent? IsoRoot(Stream image)
    {
        if (Sector(image, IsoPrimaryVolumeSector) is not { } pvd || pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
            return null;
        return new Extent(BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 2)), BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(156 + 10)));
    }

    /// <summary>A file or folder in <paramref name="folder"/>, by name (any case, without ";1").</summary>
    internal static Extent? IsoEntry(Stream image, Extent? folder, string name, bool directory) =>
        folder is { } f && Entries(image, f).FirstOrDefault(e => e.Directory == directory && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            is { Name: not null } entry
            ? entry.Extent
            : null;

    internal static byte[]? ReadExtent(Stream image, Extent extent, int max)
    {
        var length = (int)Math.Min(extent.Length, max);
        if (length <= 0 || extent.Sector * SectorSize + length > image.Length) return null;
        var bytes = new byte[length];
        image.Position = extent.Sector * SectorSize;
        image.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>The files in an image's VIDEO_TS folder (names upper case), through ISO 9660; empty if it has none.</summary>
    internal static IReadOnlyList<(string Name, long Length)> VideoTsFiles(Stream image) =>
        IsoEntry(image, IsoRoot(image), "VIDEO_TS", directory: true) is { } videoTs
            ? Entries(image, videoTs).Where(e => !e.Directory).Select(e => (e.Name, e.Extent.Length)).ToList()
            : [];

    /// <summary>The volume's name (ISO 9660 volume identifier), e.g. "THE_ONE"; null without ISO 9660.</summary>
    internal static string? VolumeLabel(Stream image) =>
        Sector(image, IsoPrimaryVolumeSector) is { } pvd && pvd[0] == 1 && Encoding.ASCII.GetString(pvd, 1, 5) == "CD001"
            ? Encoding.ASCII.GetString(pvd, 40, 32).Trim() is { Length: > 0 } label ? label : null
            : null;

    /// <summary>A folder's directory records: name (upper case, without ";1"), whether it's a folder, and where its data is.</summary>
    private static List<(string Name, bool Directory, Extent Extent)> Entries(Stream image, Extent folder)
    {
        var entries = new List<(string, bool, Extent)>();
        var length = (int)Math.Min(folder.Length, 64 * SectorSize);
        if (length <= 0 || Sector(image, folder.Sector, (length + SectorSize - 1) / SectorSize) is not { } dir) return entries;
        for (var pos = 0; pos < length;)
        {
            int recordLength = dir[pos];
            if (recordLength == 0)
            {
                pos = (pos / SectorSize + 1) * SectorSize; // records don't cross sectors
                continue;
            }
            if (pos + recordLength > dir.Length || recordLength < 34) break;
            int nameLength = dir[pos + 32];
            if (nameLength > 1 && pos + 33 + nameLength <= dir.Length)
                entries.Add((Encoding.ASCII.GetString(dir, pos + 33, nameLength).Split(';')[0].TrimEnd('.').ToUpperInvariant(),
                    (dir[pos + 25] & 2) != 0,
                    new Extent(BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(pos + 2)), BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(pos + 10)))));
            pos += recordLength;
        }
        return entries;
    }

    /// <summary>The UDF revision (0x0102, 0x0250…) from the logical volume descriptor; 0 without UDF.</summary>
    internal static int UdfRevision(Stream image)
    {
        // The anchor volume descriptor pointer (tag 2) points at the main volume descriptor sequence.
        if (Sector(image, UdfAnchorSector) is not { } avdp || BinaryPrimitives.ReadUInt16LittleEndian(avdp) != 2) return 0;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(avdp.AsSpan(16));
        var location = BinaryPrimitives.ReadUInt32LittleEndian(avdp.AsSpan(20));
        var count = (int)Math.Clamp(length / SectorSize, 1, 64);
        for (var i = 0; i < count; i++)
        {
            if (Sector(image, location + i) is not { } d) return 0;
            switch (BinaryPrimitives.ReadUInt16LittleEndian(d))
            {
                case 6: // logical volume descriptor: domain identifier at 216, its suffix's first two bytes the revision
                    return Encoding.ASCII.GetString(d, 217, 19) == "*OSTA UDF Compliant"
                        ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(216 + 24))
                        : 0;
                case 8: return 0; // terminating descriptor
            }
        }
        return 0;
    }
}
