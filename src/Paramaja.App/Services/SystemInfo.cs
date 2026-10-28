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

    /// <summary>Mains or battery (and how full): laptops on battery often slow their GPU down.</summary>
    public static string Power
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows()) return WindowsPower();
                const string supplies = "/sys/class/power_supply";
                if (!Directory.Exists(supplies)) return "unknown";
                string? Read(string dir, string file) => File.Exists(Path.Combine(dir, file)) ? File.ReadAllText(Path.Combine(dir, file)).Trim() : null;
                var dirs = Directory.GetDirectories(supplies);
                var battery = dirs.FirstOrDefault(d => Read(d, "type") == "Battery");
                if (battery is null) return "mains (no battery)";
                var mains = dirs.Any(d => Read(d, "type") == "Mains" && Read(d, "online") == "1");
                return $"{(mains ? "mains" : "battery")}, battery {Read(battery, "capacity") ?? "?"}%";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            return "unknown";
        }
    }

    /// <summary>Each display's mode as Windows reports it, refresh rate included; empty elsewhere.</summary>
    public static IReadOnlyList<string> Displays
    {
        get
        {
            try { return OperatingSystem.IsWindows() ? WindowsDisplays() : []; }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return []; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [SupportedOSPlatform("windows")]
    private static string WindowsPower()
    {
        if (!GetSystemPowerStatus(out var p)) return "unknown";
        if (p.BatteryFlag == 128) return "mains (no battery)";
        var source = p.AcLineStatus switch { 0 => "battery", 1 => "mains", _ => "unknown source" };
        return p.BatteryLifePercent <= 100 ? $"{source}, battery {p.BatteryLifePercent}%" : source;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TtOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode info);

    [SupportedOSPlatform("windows")]
    private static List<string> WindowsDisplays()
    {
        const int attachedToDesktop = 1, primary = 4, currentSettings = -1;
        var list = new List<string>();
        for (uint i = 0; ; i++)
        {
            var device = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevicesW(null, i, ref device, 0)) break;
            if ((device.StateFlags & attachedToDesktop) == 0) continue;
            var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettingsW(device.DeviceName, currentSettings, ref mode)) continue;
            list.Add($"{device.DeviceString} {mode.PelsWidth}x{mode.PelsHeight} @ {mode.DisplayFrequency} Hz" +
                     ((device.StateFlags & primary) != 0 ? " (primary)" : ""));
        }
        return list;
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
