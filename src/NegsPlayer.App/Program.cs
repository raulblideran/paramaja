using Avalonia;

namespace NegsPlayer.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Matches the desktop file, so the taskbar groups the window under the right icon.</summary>
    public const string AppId = "io.github.raulblideran.NegsPlayer";

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = AppId })
            .WithInterFont()
            .LogToTrace();
}
