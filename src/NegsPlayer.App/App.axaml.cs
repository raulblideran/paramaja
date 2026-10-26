using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NegsPlayer.App.Services;
using NegsPlayer.App.ViewModels;
using NegsPlayer.App.Views;
using NegsPlayer.Core.Playback;
using NegsPlayer.Core.Settings;
using NegsPlayer.Core.Torrent;

namespace NegsPlayer.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Set NEGS_DEBUG=1 to get a verbose mpv log in the app's logs folder.</summary>
    private static IEnumerable<KeyValuePair<string, string>> DebugOptions(PortablePaths paths)
    {
        if (Environment.GetEnvironmentVariable("NEGS_DEBUG") != "1") yield break;
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
                wid => new PlayerService(settings, paths, wid, DebugOptions(paths)),
                new TorrentService(), new AvaloniaDispatcher());

            var window = new MainWindow { DataContext = vm };
            vm.Dialogs = new DialogService(window, () => vm);
            window.Closed += (_, _) => vm.Dispose();
            desktop.MainWindow = window;

            if (desktop.Args is [var first, ..]) _ = vm.OpenInputAsync(first);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
