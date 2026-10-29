namespace Paramaja.Core.Input;

public enum InputKind { None, Magnet, Url, File, Mega, Folder, Dvd, Bluray, AudioCd, Sacd }

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
    /// then the disc's root folder. An Audio CD's drive (Track01.cda… on Windows) plays as a CD.
    /// A folder holding a CD rip's cue sheet plays the cue sheet (its tracks as chapters). Disc
    /// images play like their discs: a DVD, Blu-ray or SACD .iso (DiscImage), and an Audio CD's
    /// BIN/CUE (the value is then its cue sheet). Any other folder plays as a playlist.
    /// </summary>
    private static (InputKind, string) Local(string path)
    {
        // A raw optical drive (an unmounted disc): an Audio CD, or a DVD, which plays straight from it.
        if (IsOpticalDevice(path)) return (OpticalDisc.Probe(path) == DiscContent.Audio ? InputKind.AudioCd : InputKind.Dvd, path);
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
            if (dir is not null && Path.GetExtension(name).Equals(".cda", StringComparison.OrdinalIgnoreCase))
                return (InputKind.AudioCd, Path.GetPathRoot(full) ?? dir);
            if (Path.GetExtension(name).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            {
                // A disc image plays like the disc; any other .iso goes to mpv as a file, as before.
                return DiscImage.Probe(full) switch
                {
                    DiscImageKind.Dvd => (InputKind.Dvd, full),
                    DiscImageKind.Bluray => (InputKind.Bluray, full),
                    DiscImageKind.Sacd => (InputKind.Sacd, full),
                    _ => (InputKind.File, full),
                };
            }
            if (Path.GetExtension(name).Equals(".cue", StringComparison.OrdinalIgnoreCase) && Playback.CueSheet.IsCdImage(full))
                return (InputKind.AudioCd, full);
            // A CD image's .bin (or .img) picked rather than its cue sheet.
            if (dir is not null && Path.GetExtension(name).ToLowerInvariant() is ".bin" or ".img" && CueFor(full) is { } imageCue)
                return (InputKind.AudioCd, imageCue);
            return (InputKind.File, full);
        }
        if (IsDvd(full)) return (InputKind.Dvd, full);
        if (IsBluray(full)) return (InputKind.Bluray, full);
        if (OpticalDisc.HasCdaTracks(full)) return (InputKind.AudioCd, full);
        if (CueSheet(full) is { } cue) return (Playback.CueSheet.IsCdImage(cue) ? InputKind.AudioCd : InputKind.File, cue);
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

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mts", ".webm", ".vob", ".mpg", ".mpeg",
    };

    /// <summary>
    /// A CD rip's cue sheet in a folder of music (not one with videos): the one naming files that
    /// are there, else one whose files are there under another audio extension (EAC names the
    /// .wav it ripped to), else the first. Null if there's none.
    /// </summary>
    internal static string? CueSheet(string dir)
    {
        try
        {
            var files = Directory.EnumerateFiles(dir).ToList();
            if (files.Any(f => VideoExtensions.Contains(Path.GetExtension(f)))) return null;
            var cues = files.Where(f => Path.GetExtension(f).Equals(".cue", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(c => (Path: c, Tracks: Playback.CueSheet.Read(c).Tracks))
                .ToList();
            return (cues.FirstOrDefault(c => c.Tracks.Count > 0 && c.Tracks.All(t => File.Exists(Path.Combine(dir, Playback.CueSheet.FileName(t.File)))))
                    .Path
                ?? cues.FirstOrDefault(c => c.Tracks.Count > 0 && c.Tracks.All(t => Playback.CueSheet.Resolve(dir, t.File) is not null)).Path)
                ?? cues.FirstOrDefault().Path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The cue sheet next to a CD image's .bin that names it, if it's an audio CD's.</summary>
    private static string? CueFor(string image)
    {
        try
        {
            var dir = Path.GetDirectoryName(image)!;
            return Directory.EnumerateFiles(dir)
                .Where(f => Path.GetExtension(f).Equals(".cue", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(c =>
                {
                    var sheet = Playback.CueSheet.Read(c);
                    return Playback.CueSheet.IsCdImage(sheet) && sheet.Tracks.Any(t =>
                        string.Equals(Playback.CueSheet.FileName(t.File), Path.GetFileName(image), StringComparison.OrdinalIgnoreCase));
                });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsMegaHost(string host) =>
        host is "mega.nz" or "www.mega.nz" or "mega.co.nz" or "www.mega.co.nz" or "mega.io";

    /// <summary>Streams that should buffer in bigger chunks (everything that isn't a local file).</summary>
    public static bool IsStreaming(InputKind kind) => kind is InputKind.Url or InputKind.Magnet or InputKind.Mega;
}
