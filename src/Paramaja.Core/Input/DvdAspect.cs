using System.Buffers.Binary;
using System.Text;

namespace Paramaja.Core.Input;

/// <summary>
/// Each DVD title's shape, 4:3 or 16:9, as its IFO files give it. libVLC doesn't say what shape a
/// DVD's picture is (its 720×576 pixels aren't square), so the video menu's Resize / Crop choices
/// go by this. The disc may be a folder, an image (.iso) or a drive: images and drives are read
/// through their ISO 9660 file system.
/// </summary>
public static class DvdAspect
{
    private const int Sector = 2048;

    /// <summary>DVD title number (1, 2…) → width over height; empty where the IFOs can't be read.</summary>
    public static IReadOnlyDictionary<int, double> TitleAspects(string root)
    {
        try
        {
            if (Directory.Exists(root)) return Read(name => FromFolder(root, name));
            using var image = new FileStream(root, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            return Read(name => FromIso(image, name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            return new Dictionary<int, double>();
        }
    }

    /// <summary>
    /// VIDEO_TS.IFO's title table (TT_SRPT, at the sector its offset 0xC4 gives) says which title
    /// set each title is in; each set's VTS_nn_0.IFO has its video attributes at 0x200, whose bits
    /// 3-2 are the shape: 0 for 4:3, 3 for 16:9.
    /// </summary>
    internal static Dictionary<int, double> Read(Func<string, byte[]?> file)
    {
        var aspects = new Dictionary<int, double>();
        if (file("VIDEO_TS.IFO") is not { Length: >= Sector } vmg || Encoding.ASCII.GetString(vmg, 0, 12) != "DVDVIDEO-VMG") return aspects;
        var table = (int)BinaryPrimitives.ReadUInt32BigEndian(vmg.AsSpan(0xC4)) * Sector;
        if (table <= 0 || table + 8 > vmg.Length) return aspects;
        var titles = BinaryPrimitives.ReadUInt16BigEndian(vmg.AsSpan(table));
        var sets = new Dictionary<int, double?>();
        for (var t = 0; t < titles && table + 8 + 12 * (t + 1) <= vmg.Length; t++)
        {
            var set = vmg[table + 8 + 12 * t + 6];
            if (!sets.TryGetValue(set, out var aspect))
                sets[set] = aspect = file($"VTS_{set:00}_0.IFO") is { Length: > 0x201 } vts && Encoding.ASCII.GetString(vts, 0, 12) == "DVDVIDEO-VTS"
                    ? ((vts[0x200] >> 2) & 3) == 3 ? 16.0 / 9 : 4.0 / 3
                    : null;
            if (aspect is { } a) aspects[t + 1] = a;
        }
        return aspects;
    }

    private static byte[]? FromFolder(string root, string name)
    {
        var dir = Directory.EnumerateDirectories(root).FirstOrDefault(d => Path.GetFileName(d).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase));
        var path = dir is null ? null : Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
        if (path is null) return null;
        using var f = File.OpenRead(path);
        var bytes = new byte[Math.Min(f.Length, 64 * Sector)];
        f.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>A file in the image's VIDEO_TS folder, through ISO 9660 (DVD-Video always has it beside UDF).</summary>
    private static byte[]? FromIso(Stream image, string name) =>
        DiscImage.IsoEntry(image, DiscImage.IsoRoot(image), "VIDEO_TS", directory: true) is { } videoTs
        && DiscImage.IsoEntry(image, videoTs, name, directory: false) is { } entry
            ? DiscImage.ReadExtent(image, entry, 64 * Sector)
            : null;
}
