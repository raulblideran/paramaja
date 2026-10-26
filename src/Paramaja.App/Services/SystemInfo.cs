using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paramaja.App.Services;

/// <summary>Machine details for the diagnostics report: what a "4K lags here but not there" question needs.</summary>
public static class SystemInfo
{
    public static string Os => RuntimeInformation.OSDescription;

    public static string Memory
    {
        get
        {
            var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return bytes > 0 ? $"{bytes / (1024.0 * 1024 * 1024):0.#} GB" : "unknown";
        }
    }

    public static string Cpu
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows()) return WindowsCpu() ?? "unknown";
                if (File.Exists("/proc/cpuinfo"))
                    return File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2)[1].Trim() ?? "unknown";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            return "unknown";
        }
    }

    public static IReadOnlyList<string> Gpus
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows()) return WindowsGpus();
                if (Directory.Exists("/sys/class/drm"))
                    return Directory.GetDirectories("/sys/class/drm", "card?")
                        .Select(d => Path.Combine(d, "device"))
                        .Where(d => File.Exists(Path.Combine(d, "vendor")))
                        .Select(d => $"{VendorName(File.ReadAllText(Path.Combine(d, "vendor")).Trim())} device {File.ReadAllText(Path.Combine(d, "device")).Trim()}")
                        .Distinct().ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            return [];
        }
    }

    internal static string VendorName(string pciVendor) => pciVendor.ToLowerInvariant() switch
    {
        "0x10de" => "NVIDIA",
        "0x1002" => "AMD",
        "0x8086" => "Intel",
        var v => v,
    };

    [SupportedOSPlatform("windows")]
    private static string? WindowsCpu()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim();
    }

    /// <summary>Display adapters from the driver class key; no WMI dependency needed.</summary>
    [SupportedOSPlatform("windows")]
    private static List<string> WindowsGpus()
    {
        const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(displayClass);
        if (root is null) return [];
        var list = new List<string>();
        foreach (var name in root.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            using var k = root.OpenSubKey(name);
            if (k?.GetValue("DriverDesc") is string desc)
                list.Add(k.GetValue("DriverVersion") is string ver ? $"{desc} (driver {ver})" : desc);
        }
        return list.Distinct().ToList();
    }
}
