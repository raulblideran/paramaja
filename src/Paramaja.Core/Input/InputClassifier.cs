namespace Paramaja.Core.Input;

public enum InputKind { None, Magnet, Url, File, Mega, Folder, Dvd, Bluray }

/// <summary>Decides what a pasted/dropped/command-line string is.</summary>
public static class InputClassifier
{
    public static (InputKind Kind, string Value) Classify(string? text)
    {
        var t = text?.Trim().Trim('"') ?? "";
        if (t.Length == 0) return (InputKind.None, "");

        if (t.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return (InputKind.Magnet, t);
        if (t.Length == 40 && t.All(Uri.IsHexDigit)) return (InputKind.Magnet, $"magnet:?xt=urn:btih:{t.ToLowerInvariant()}");

        if (Uri.TryCreate(t, UriKind.Absolute, out var uri))
        {
            // MEGA files are encrypted; they go through the engine, which decrypts them.
            if (uri.Scheme is "http" or "https" && IsMegaHost(uri.Host)) return (InputKind.Mega, t);
            if (uri.IsFile) return Local(uri.LocalPath);
            if (uri.Scheme is "http" or "https" or "rtsp" or "rtmp" or "rtp" or "udp" or "srt" or "smb" or "ftp")
                return (InputKind.Url, t);
        }

        return Local(t);
    }

    /// <summary>
    /// A local file or folder. DVD (VIDEO_TS) and Blu-ray (BDMV) folders, and their
    /// VIDEO_TS.IFO / index.bdmv / .mpls files, play as discs (the main title); the value is
    /// then the disc's root folder. Any other folder plays as a playlist.
    /// </summary>
    private static (InputKind, string) Local(string path)
    {
        // A raw optical drive (an unmounted disc): DVDs play straight from it.
        if (IsOpticalDevice(path)) return (InputKind.Dvd, path);
        if (!File.Exists(path) && !Directory.Exists(path)) return (InputKind.None, "");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // A drive root keeps its separator: "D:" alone means "the current folder on D:".
        if (full.Length == 0 || full.EndsWith(':')) full = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        if (File.Exists(full))
        {
            var name = Path.GetFileName(full);
            var dir = Path.GetDirectoryName(full);
            if (dir is not null && name.Equals("VIDEO_TS.IFO", StringComparison.OrdinalIgnoreCase))
                return DiscRoot(dir, "VIDEO_TS") is { } dvd ? (InputKind.Dvd, dvd) : (InputKind.File, full);
            if (dir is not null && (name.Equals("index.bdmv", StringComparison.OrdinalIgnoreCase)
                                    || Path.GetExtension(name).Equals(".mpls", StringComparison.OrdinalIgnoreCase)))
                return FindBluray(dir) is { } bd ? (InputKind.Bluray, bd) : (InputKind.File, full);
            return (InputKind.File, full);
        }
        if (IsDvd(full)) return (InputKind.Dvd, full);
        if (IsBluray(full)) return (InputKind.Bluray, full);
        // The VIDEO_TS or BDMV folder itself was picked: the disc is its parent.
        if (Path.GetFileName(full).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(full) is { } dvdParent && IsDvd(dvdParent))
            return (InputKind.Dvd, dvdParent);
        if (FindBluray(full) is { } root) return (InputKind.Bluray, root);
        return (InputKind.Folder, full);
    }

    internal static bool IsOpticalDevice(string path) =>
        !OperatingSystem.IsWindows()
        && (path.StartsWith("/dev/sr", StringComparison.Ordinal) || path is "/dev/cdrom" or "/dev/dvd")
        && File.Exists(path);

    private static bool IsDvd(string root) =>
        Child(root, "VIDEO_TS") is { } v && Child(v, "VIDEO_TS.IFO", file: true) is not null;

    private static bool IsBluray(string root) =>
        Child(root, "BDMV") is { } b && Child(b, "index.bdmv", file: true) is not null;

    /// <summary>The disc root for a folder inside a Blu-ray (BDMV, or BDMV/PLAYLIST).</summary>
    private static string? FindBluray(string dir)
    {
        for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
        {
            if (IsBluray(d)) return d;
            if (!Path.GetFileName(d).Equals("BDMV", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(d).Equals("PLAYLIST", StringComparison.OrdinalIgnoreCase)) return null;
        }
        return null;
    }

    private static string? DiscRoot(string dir, string folder) =>
        Path.GetFileName(dir).Equals(folder, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(dir) is { } parent
            ? parent
            : null;

    /// <summary>Disc folder names are upper case on the disc, but copies may differ.</summary>
    private static string? Child(string dir, string name, bool file = false)
    {
        try
        {
            var entries = file ? Directory.EnumerateFiles(dir) : Directory.EnumerateDirectories(dir);
            return entries.FirstOrDefault(e => Path.GetFileName(e).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsMegaHost(string host) =>
        host is "mega.nz" or "www.mega.nz" or "mega.co.nz" or "www.mega.co.nz" or "mega.io";

    /// <summary>Streams that should buffer in bigger chunks (everything that isn't a local file).</summary>
    public static bool IsStreaming(InputKind kind) => kind is InputKind.Url or InputKind.Magnet or InputKind.Mega;
}
