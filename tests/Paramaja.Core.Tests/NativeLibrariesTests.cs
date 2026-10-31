using Paramaja.Core.Native;

namespace Paramaja.Core.Tests;

public class NativeLibrariesTests
{
    [Fact]
    public void App_folder_comes_first_and_the_system_loader_before_dev_fallbacks()
    {
        var order = NativeLibraries.ProbeOrder(NativeLibraries.Mpv).ToList();
        Assert.StartsWith("dir:" + AppContext.BaseDirectory, order[0]);

        var firstSystem = order.FindIndex(p => p.StartsWith("system:"));
        Assert.True(firstSystem > 0);
        Assert.DoesNotContain(order.Take(firstSystem), p => p.Contains("linuxbrew") || p.Contains("/usr/local"));
        if (!OperatingSystem.IsWindows())
            Assert.Contains(order.Skip(firstSystem), p => p.Contains("linuxbrew"));
    }

    [Fact]
    public void Platform_library_names()
    {
        var names = NativeLibraries.CandidateNames(NativeLibraries.Mpv).ToList();
        Assert.Contains(OperatingSystem.IsWindows() ? "libmpv-2.dll" : "libmpv.so.2", names);
        // libdvdread as the Windows build bundles it (packaging/dvd/win64) and as the Flatpak builds it.
        Assert.Contains(OperatingSystem.IsWindows() ? "libdvdread-8.dll" : "libdvdread.so.8",
            NativeLibraries.CandidateNames(NativeLibraries.DvdRead));
    }
}
