using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Paramaja.App.ViewModels;

namespace Paramaja.App.Views;

public partial class RipDialog : Window
{
    public RipDialog() => InitializeComponent();

    private RipDialogViewModel? Vm => DataContext as RipDialogViewModel;

    private static readonly FilePickerFileType Images = new("DVD images") { Patterns = ["*.iso", "*.img"] };

    private async void OnPickDvdImage(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "DVD-Video image (.iso)",
            AllowMultiple = false,
            FileTypeFilter = [Images, FilePickerFileTypes.All],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) Vm?.UseDvdSource(path);
    }

    private async void OnPickDvdFolder(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("A DVD's VIDEO_TS folder (or the folder it's in)") is { } path) Vm?.UseDvdSource(path);
    }

    private async void OnPickCdFolder(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && await PickFolder("Save ripped CDs in") is { } path) vm.CdFolder = path;
    }

    private async void OnPickDvdOutput(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && await PickFolder("Save ripped DVDs in") is { } path) vm.DvdFolder = path;
    }

    private async Task<string?> PickFolder(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private void OnMkvMode(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.DvdModeIndex = 0;
    }

    private void OnDecryptMode(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.DvdModeIndex = 1;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FitToScreen();
    }

    /// <summary>
    /// No taller or wider than the screen it opens on (the player's), and all of it on that screen:
    /// see <see cref="RipDialogLayout"/>.
    /// </summary>
    private void FitToScreen()
    {
        var screen = (Owner is Window owner ? Screens.ScreenFromWindow(owner) : null) ?? Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var fit = RipDialogLayout.Fit(screen.WorkingArea, screen.Scaling, Width);
        MaxHeight = fit.Height;
        Width = fit.Width;
        // Once it has shrunk to fit: centred over the player at its first size, it may hang off the screen.
        Dispatcher.UIThread.Post(() =>
        {
            // Windows reports where the frame is, X11 where the window's inside is (the title bar above it).
            var titleBar = OperatingSystem.IsWindows() ? 0 : (int)Math.Round(RipDialogLayout.TitleBar * screen.Scaling);
            var size = PixelSize.FromSize(OperatingSystem.IsWindows() ? FrameSize ?? Bounds.Size : Bounds.Size, screen.Scaling);
            var inside = RipDialogLayout.KeepInside(Position, size, screen.WorkingArea, titleBar);
            if (inside != Position) Position = inside;
        }, DispatcherPriority.Background);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Vm?.Dispose();
    }
}
