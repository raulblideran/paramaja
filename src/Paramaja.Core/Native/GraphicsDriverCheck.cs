using System.Text.RegularExpressions;

namespace Paramaja.Core.Native;

/// <summary>A reason video can't use the graphics card, and how to fix it.</summary>
/// <param name="Command">The command that fixes it, to copy into a terminal.</param>
public sealed record GraphicsProblem(string Summary, string Command);

/// <summary>
/// What the Flatpak needs from the machine's graphics driver. Inside the sandbox the GPU is
/// reached through the runtime's own drivers: Mesa (AMD, Intel, nouveau) comes with it, but
/// NVIDIA's proprietary driver doesn't. It needs the runtime's GL extension built for exactly the
/// host's driver version (org.freedesktop.Platform.GL.nvidia-580-95-05 for 580.95.05). Flatpak
/// installs it with the runtime when Flathub has it, but rolling distributions (Arch, CachyOS)
/// often run a driver newer than any Flathub has built yet, or a beta. Without it Vulkan and
/// OpenGL fall back to the CPU (llvmpipe, lavapipe): the window opens, video crawls or stays black.
/// </summary>
public static partial class GraphicsDriverCheck
{
    /// <summary>Where the runtime mounts its GL extensions (default, nvidia-…).</summary>
    public const string ExtensionsDir = "/usr/lib/x86_64-linux-gnu/GL";

    /// <summary>
    /// The NVIDIA driver extension this sandbox misses, if it does. <paramref name="exists"/> and
    /// <paramref name="read"/> are the file system (tests pass their own). Outside the Flatpak, or
    /// without an NVIDIA driver, there's nothing to check.
    /// </summary>
    public static GraphicsProblem? MissingNvidiaExtension(string? flatpakId, Func<string, bool> exists, Func<string, string?> read)
    {
        if (string.IsNullOrEmpty(flatpakId)) return null;
        if (!exists("/dev/nvidiactl") && !exists("/dev/nvidia0")) return null;
        if (NvidiaVersion(read("/proc/driver/nvidia/version")) is not { } version) return null;
        var dashed = version.Replace('.', '-');
        if (exists($"{ExtensionsDir}/nvidia-{dashed}")) return null;
        return new GraphicsProblem(
            $"Your NVIDIA driver is {version}, but the Flatpak's matching driver extension (GL.nvidia-{dashed}) isn't installed, "
            + "so Paramaja can't use your graphics card: video is drawn by the CPU, slowly, or not at all.",
            $"flatpak install flathub org.freedesktop.Platform.GL.nvidia-{dashed}");
    }

    /// <summary>
    /// The same, on this machine. PARAMAJA_FAKE_NVIDIA=&lt;version&gt; acts as if it had that NVIDIA
    /// driver, to see what someone with one would.
    /// </summary>
    public static GraphicsProblem? MissingNvidiaExtension()
    {
        var flatpak = Environment.GetEnvironmentVariable("FLATPAK_ID");
        if (Environment.GetEnvironmentVariable("PARAMAJA_FAKE_NVIDIA") is not { Length: > 0 } fake)
            return MissingNvidiaExtension(flatpak, Exists, Read);
        return MissingNvidiaExtension(flatpak ?? "fake", p => p == "/dev/nvidiactl" || Exists(p),
            p => p == "/proc/driver/nvidia/version" ? $"NVRM version: NVIDIA UNIX x86_64 Kernel Module  {fake}  Release Build" : Read(p));
    }

    /// <summary>"580.95.05" from /proc/driver/nvidia/version's first line (proprietary or open kernel module).</summary>
    internal static string? NvidiaVersion(string? proc) =>
        proc is not null && VersionLine().Match(proc) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"Kernel Module(?: for \S+)?\s+(\d+(?:\.\d+)+)")]
    private static partial Regex VersionLine();

    /// <summary>For the log: the GL extensions this sandbox has, and the NVIDIA driver, if any.</summary>
    public static string Describe()
    {
        var parts = new List<string>();
        try
        {
            if (Directory.Exists(ExtensionsDir))
            {
                var extensions = Directory.GetDirectories(ExtensionsDir).Select(Path.GetFileName)
                    .Where(n => n is not ("lib" or "glvnd" or "vulkan" or "OpenCL" or "vdpau")).Order().ToList();
                parts.Add($"GL extensions: {(extensions.Count == 0 ? "none" : string.Join(", ", extensions))}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (Exists("/dev/nvidiactl"))
            parts.Add($"NVIDIA driver {NvidiaVersion(Read("/proc/driver/nvidia/version")) ?? "(version unknown)"}");
        if (MissingNvidiaExtension() is { } problem) parts.Add($"missing: {problem.Command}");
        return string.Join("; ", parts);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
