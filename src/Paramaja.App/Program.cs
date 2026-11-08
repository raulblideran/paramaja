using Avalonia;
using Paramaja.App.Services;
using Paramaja.Core.Settings;

namespace Paramaja.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);
        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Tell(NoDisplay);
            return 1;
        }
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            var log = WriteCrash(e);
            Tell($"Paramaja couldn't start: {e.Message}" + (log is null ? "" : $"\nDetails: {log}"));
            return 1;
        }
    }

    /// <summary>Matches the desktop file, so the taskbar groups the window under the right icon.</summary>
    public const string AppId = "io.github.raulblideran.Paramaja";

    /// <summary>
    /// The window is X11 (Avalonia 11 has no Wayland backend): a Wayland session needs Xwayland,
    /// which KDE and GNOME always run, but some compositors (niri, sway or Hyprland set up
    /// without it) don't.
    /// </summary>
    internal const string NoDisplay =
        "Paramaja needs an X11 display (on a Wayland desktop, Xwayland), and there's none (DISPLAY isn't set). "
        + "KDE and GNOME run Xwayland by themselves; on sway, Hyprland or niri, install and enable Xwayland "
        + "(xwayland-satellite for niri), then start Paramaja again.";

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                WmClass = AppId,
                // GLX first, as before; where it fails (a GPU driver the Flatpak can't reach) EGL,
                // then drawing on the CPU, rather than no window at all.
                RenderingMode = [X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software],
            })
            .WithInterFont()
            .LogToTrace();

    /// <summary>The exception in logs/crash.log (next to the app, or the Flatpak's cache); its path, or null.</summary>
    internal static string? WriteCrash(Exception? e)
    {
        if (e is null) return null;
        try
        {
            var paths = PortablePaths.ForApp();
            Directory.CreateDirectory(paths.Logs);
            var file = Path.Combine(paths.Logs, "crash.log");
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {AppInfo.Name} {AppInfo.Display} on "
                                     + $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n{e}\n\n");
            return file;
        }
        catch (Exception) { return null; } // nowhere to write: stderr has it
    }

    /// <summary>Says it on stderr, and as a desktop notification where there's notify-send (no window can open).</summary>
    private static void Tell(string message)
    {
        Console.Error.WriteLine(message);
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notify-send", ["Paramaja", message])
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
        }
        catch (Exception) { } // no notify-send: stderr is all there is
    }
}
