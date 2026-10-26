using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paramaja.Core.Settings;

/// <summary>
/// Where the app keeps its files. On Windows (and any plain copy of the app) everything lives
/// next to the executable, so the folder can be moved around and nothing touches AppData or the
/// registry. Inside a Flatpak the app folder is read-only, so the sandbox's own XDG directories
/// are used instead and screenshots go to the user's Pictures folder. Downloaded videos always
/// go to the user's Downloads folder, where people look for them.
/// </summary>
public sealed partial class PortablePaths
{
    public PortablePaths(string root)
        : this(root, Path.Combine(root, "Screenshots"), Path.Combine(root, "cache"), Path.Combine(root, "logs"))
    {
    }

    private PortablePaths(string root, string screenshots, string torrentCache, string logs)
    {
        Root = Path.GetFullPath(root);
        Screenshots = Path.GetFullPath(screenshots);
        TorrentCache = Path.GetFullPath(torrentCache);
        Logs = Path.GetFullPath(logs);
    }

    public static PortablePaths ForApp() =>
        Environment.GetEnvironmentVariable("FLATPAK_ID") is { Length: > 0 }
            ? ForFlatpak(
                Xdg("XDG_CONFIG_HOME", ".config"),
                Xdg("XDG_CACHE_HOME", ".cache"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } pics
                    ? pics
                    : Path.Combine(Home, "Pictures"))
            : new PortablePaths(AppContext.BaseDirectory);

    /// <param name="configHome">The sandbox's XDG_CONFIG_HOME (~/.var/app/&lt;id&gt;/config), already per-app.</param>
    /// <param name="cacheHome">The sandbox's XDG_CACHE_HOME, where torrent data belongs.</param>
    /// <param name="pictures">The user's Pictures folder; screenshots go in a subfolder of it.</param>
    public static PortablePaths ForFlatpak(string configHome, string cacheHome, string pictures) =>
        new(configHome,
            Path.Combine(pictures, "Paramaja"),
            Path.Combine(cacheHome, "torrents"),
            Path.Combine(cacheHome, "logs"));

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } v ? v : Path.Combine(Home, fallback);

    /// <summary>Holds settings.json and mpv's config dir.</summary>
    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string Screenshots { get; }
    public string TorrentCache { get; }
    /// <summary>mpv's own config dir (mpv.conf, input.conf, shaders) — optional power-user hook.</summary>
    public string MpvConfig => Path.Combine(Root, "portable_config");
    public string Logs { get; }
    /// <summary>Where downloaded videos are saved: the system's Downloads folder.</summary>
    public string Downloads { get; init; } = SystemDownloads();

    internal static string SystemDownloads()
    {
        try
        {
            if (OperatingSystem.IsWindows() && WindowsDownloads() is { } win) return win;
            if (!OperatingSystem.IsWindows() && XdgDownloads(Path.Combine(Home, ".config", "user-dirs.dirs"), Home) is { } xdg) return xdg;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException) { }
        return Path.Combine(Home, "Downloads");
    }

    /// <summary>XDG_DOWNLOAD_DIR from user-dirs.dirs, e.g. <c>"$HOME/Téléchargements"</c>.</summary>
    internal static string? XdgDownloads(string userDirsFile, string home)
    {
        if (!File.Exists(userDirsFile)) return null;
        var line = File.ReadLines(userDirsFile).FirstOrDefault(l => l.TrimStart().StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal));
        if (line is null) return null;
        var value = line.Split('=', 2)[1].Trim().Trim('"').Replace("$HOME", home);
        return Path.IsPathRooted(value) && value.TrimEnd('/') != home.TrimEnd('/') ? value : null;
    }

    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    [SupportedOSPlatform("windows")]
    private static string? WindowsDownloads()
    {
        if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var p) < 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public void EnsureCreated()
    {
        foreach (var d in new[] { Screenshots, TorrentCache, MpvConfig }) Directory.CreateDirectory(d);
    }
}
