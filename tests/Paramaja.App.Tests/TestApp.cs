using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;

[assembly: AvaloniaTestApplication(typeof(Paramaja.App.Tests.TestApp))]

namespace Paramaja.App.Tests;

public static class TestApp
{
    /// <summary>
    /// The app's own Inter font, as in Program.cs, and as the default family: headless Skia on
    /// Windows has no system font collection to fall back on ("fonts:SystemFonts" not found).
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" });
}
