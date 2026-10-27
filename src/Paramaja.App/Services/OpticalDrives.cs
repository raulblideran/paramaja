namespace Paramaja.App.Services;

/// <summary>A DVD / Blu-ray drive. <see cref="Root"/> is what to open: its mounted folder, or the raw drive.</summary>
public sealed record OpticalDrive(string Root, string Name, string? DiscLabel)
{
    public bool HasDisc => DiscLabel is not null;
    public string Description => HasDisc ? $"{Name} — {DiscLabel}" : $"{Name} — no disc";
}

/// <summary>Finds the optical drives, and the discs in them.</summary>
public static class OpticalDrives
{
    public static IReadOnlyList<OpticalDrive> Find()
    {
        try
        {
            return OperatingSystem.IsWindows() ? Windows() : Linux("/proc/mounts", Directory.GetFiles("/dev", "sr*"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static List<OpticalDrive> Windows() =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.CDRom)
            .Select(d => new OpticalDrive(d.RootDirectory.FullName, d.Name.TrimEnd('\\'),
                d.IsReady ? (string.IsNullOrWhiteSpace(d.VolumeLabel) ? "disc" : d.VolumeLabel) : null))
            .ToList();

    /// <summary>
    /// /dev/sr0, /dev/sr1…: a disc the desktop has mounted opens from its mount point (named
    /// after the disc); an unmounted one opens from the raw drive, which DVDs allow.
    /// </summary>
    internal static List<OpticalDrive> Linux(string mountsFile, IEnumerable<string> devices)
    {
        var mounts = File.Exists(mountsFile)
            ? File.ReadLines(mountsFile)
                .Select(l => l.Split(' '))
                .Where(f => f.Length > 1)
                .GroupBy(f => f[0]).ToDictionary(g => g.Key, g => Unescape(g.First()[1]))
            : [];
        return devices.Order().Select(dev =>
        {
            var name = Path.GetFileName(dev);
            return mounts.TryGetValue(dev, out var mountPoint)
                ? new OpticalDrive(mountPoint, name, Path.GetFileName(mountPoint))
                : new OpticalDrive(dev, name, "not mounted (DVDs play straight from the drive)");
        }).ToList();
    }

    /// <summary>/proc/mounts writes spaces in paths as \040.</summary>
    private static string Unescape(string path) => path.Replace("\\040", " ");
}
