using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Paramaja.App.Tests.TestApp))]

namespace Paramaja.App.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}
