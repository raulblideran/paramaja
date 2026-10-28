using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Paramaja.App.Services;
using Paramaja.App.ViewModels;
using Paramaja.App.Views;
using Paramaja.Core.Playback;
using Paramaja.Core.Settings;
using Paramaja.Core.Torrent;

namespace Paramaja.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The high-performance GPU on laptops with two (Windows), and with PARAMAJA_DEBUG=1 a
    /// verbose mpv log in the app's logs folder.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> ExtraOptions(PlayerSettings settings, PortablePaths paths)
    {
        if (OperatingSystem.IsWindows() && settings.PreferHighPerformanceGpu && GpuAdapters.PreferredD3D11Adapter() is { } adapter)
            yield return new("d3d11-adapter", adapter);
        if (Environment.GetEnvironmentVariable("PARAMAJA_DEBUG") != "1") yield break;
        Directory.CreateDirectory(paths.Logs);
        yield return new("log-file", Path.Combine(paths.Logs, "mpv.log"));
        yield return new("msg-level", "all=v");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = PortablePaths.ForApp();
            var store = new SettingsStore(paths.SettingsFile);
            var settings = store.Load();
            var vm = new MainViewModel(settings, store, paths,
                wid => new PlayerService(settings, paths, wid, ExtraOptions(settings, paths)),
                new TorrentService(), new AvaloniaDispatcher(),
                discPlayerFactory: wid => new VlcDiscPlayer(settings, paths, wid));

            // libVLC (discs) builds its plugin cache now, in the background, rather than when a disc opens.
            _ = VlcDiscPlayer.PrepareAsync();

            var window = new MainWindow { DataContext = vm };
            vm.Dialogs = new DialogService(window, () => vm);
            window.Closed += (_, _) => vm.Dispose();
            desktop.MainWindow = window;

            if (desktop.Args is [var first, ..]) _ = vm.OpenInputAsync(first);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
