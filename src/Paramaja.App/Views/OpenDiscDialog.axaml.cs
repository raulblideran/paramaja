using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Paramaja.App.Services;

namespace Paramaja.App.Views;

/// <summary>Picks a disc to play: a drive with a disc in it, a VIDEO_TS / BDMV folder, or a disc image. Returns its path.</summary>
public partial class OpenDiscDialog : Window
{
    public OpenDiscDialog()
    {
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        var drives = OpticalDrives.Find();
        DriveList.ItemsSource = drives;
        NoDrives.IsVisible = drives.Count == 0;
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => Refresh();

    private void OnPlayDrive(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string root }) Close(root);
    }

    private void OnRipDrive(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string root }) Close(ViewModels.MainViewModel.RipPrefix + root);
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "DVD (VIDEO_TS), Blu-ray (BDMV) or CD rip (.cue) folder",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) Close(path);
    }

    private static readonly FilePickerFileType Images = new("Disc images")
    {
        Patterns = ["*.iso", "*.cue", "*.bin", "*.img"],
    };

    private async void OnPickImage(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "DVD, Blu-ray or SACD image (.iso), or Audio CD image (.cue / .bin)",
            AllowMultiple = false,
            FileTypeFilter = [Images, FilePickerFileTypes.All],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) Close(path);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
