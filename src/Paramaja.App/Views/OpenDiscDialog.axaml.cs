using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Paramaja.App.Services;

namespace Paramaja.App.Views;

/// <summary>Picks a disc to play: a drive with a disc in it, or a VIDEO_TS / BDMV folder. Returns its path.</summary>
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

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "DVD (VIDEO_TS) or Blu-ray (BDMV) folder",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) Close(path);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
