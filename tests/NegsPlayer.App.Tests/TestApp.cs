using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(NegsPlayer.App.Tests.TestApp))]

namespace NegsPlayer.App.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}
