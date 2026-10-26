using System.Reflection;
using System.Runtime.InteropServices;

namespace NegsPlayer.Core.Native;

/// <summary>
/// Resolves the native libraries (libmpv and negs_torrent) from the app folder first, so the
/// portable build only needs the DLLs sitting next to the exe, then from well-known system paths.
/// </summary>
public static class NativeLibraries
{
    public const string Mpv = "mpv";
    public const string Torrent = "negs_torrent";

    private static int _registered;

    /// <summary>Extra directories to probe (tests point this at the Rust build output).</summary>
    public static List<string> SearchDirectories { get; } = [];

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
    }

    internal static IEnumerable<string> CandidateNames(string library) => library switch
    {
        Mpv when OperatingSystem.IsWindows() => ["libmpv-2.dll", "mpv-2.dll", "mpv-1.dll"],
        Mpv when OperatingSystem.IsMacOS() => ["libmpv.2.dylib", "libmpv.dylib"],
        Mpv => ["libmpv.so.2", "libmpv.so"],
        Torrent when OperatingSystem.IsWindows() => ["negs_torrent.dll"],
        Torrent when OperatingSystem.IsMacOS() => ["libnegs_torrent.dylib"],
        Torrent => ["libnegs_torrent.so"],
        _ => [library],
    };

    /// <summary>Directories that belong to this app: always searched first.</summary>
    private static IEnumerable<string> AppDirectories()
    {
        yield return AppContext.BaseDirectory;
        foreach (var d in SearchDirectories) yield return d;
        var env = Environment.GetEnvironmentVariable("NEGS_NATIVE_DIR");
        if (!string.IsNullOrEmpty(env)) yield return env;
    }

    /// <summary>
    /// Last-resort locations for a development checkout. They come after the system loader:
    /// inside the Flatpak the host's /home is visible, and a Homebrew libmpv found there would be
    /// loaded against the sandbox's libraries instead of the one built into /app/lib.
    /// </summary>
    private static IEnumerable<string> FallbackDirectories()
    {
        if (OperatingSystem.IsWindows()) yield break;
        yield return "/home/linuxbrew/.linuxbrew/lib";
        yield return "/usr/local/lib";
    }

    /// <summary>The order every load attempt follows, as "dir:&lt;path&gt;" or "system:&lt;name&gt;".</summary>
    internal static IEnumerable<string> ProbeOrder(string library)
    {
        foreach (var name in CandidateNames(library))
            foreach (var dir in AppDirectories())
                yield return "dir:" + Path.Combine(dir, name);
        foreach (var name in CandidateNames(library))
            yield return "system:" + name;
        foreach (var name in CandidateNames(library))
            foreach (var dir in FallbackDirectories())
                yield return "dir:" + Path.Combine(dir, name);
    }

    private static IntPtr Resolve(string library, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (library is not (Mpv or Torrent)) return IntPtr.Zero;
        foreach (var probe in ProbeOrder(library))
        {
            if (probe.StartsWith("dir:"))
            {
                var path = probe[4..];
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var h)) return h;
            }
            else if (NativeLibrary.TryLoad(probe[7..], assembly, searchPath, out var handle))
            {
                return handle;
            }
        }
        return IntPtr.Zero;
    }
}
