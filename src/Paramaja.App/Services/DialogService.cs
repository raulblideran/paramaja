using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Paramaja.App.ViewModels;
using Paramaja.App.Views;

namespace Paramaja.App.Services;

public sealed class DialogService(Window owner, Func<MainViewModel> vm) : IDialogService
{
    private static readonly FilePickerFileType Media = new("Video & audio")
    {
        Patterns = ["*.mkv", "*.mp4", "*.m4v", "*.avi", "*.mov", "*.wmv", "*.ts", "*.m2ts", "*.mts", "*.webm", "*.flv",
                    "*.mpg", "*.mpeg", "*.vob", "*.ogv", "*.3gp", "*.hevc", "*.h265", "*.rmvb", "*.iso", "*.mka",
                    "*.flac", "*.mp3", "*.m4a", "*.opus", "*.ogg", "*.wav", "*.ac3", "*.dts", "*.thd", "*.y4m",
                    "*.ifo", "*.bdmv", "*.mpls"],
    };

    private static readonly FilePickerFileType Subs = new("Subtitles")
    {
        Patterns = ["*.srt", "*.ass", "*.ssa", "*.sub", "*.idx", "*.vtt", "*.sup"],
    };

    public async Task<string?> PickMediaFileAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open video",
            AllowMultiple = false,
            FileTypeFilter = [Media, FilePickerFileTypes.All],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>A folder of videos, or a DVD (VIDEO_TS) or Blu-ray (BDMV) folder.</summary>
    public async Task<string?> PickFolderAsync()
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open folder, DVD or Blu-ray",
            AllowMultiple = false,
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSubtitleFileAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load subtitles",
            AllowMultiple = false,
            FileTypeFilter = [Subs, FilePickerFileTypes.All],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> AskLinkAsync()
    {
        var clip = await ReadClipboardTextAsync();
        return await new OpenLinkDialog(new OpenLinkDialogViewModel(clip)).ShowDialog<string?>(owner);
    }

    public async Task ShowDownloaderAsync(string? url)
    {
        var initial = url ?? await ReadClipboardTextAsync();
        await new DownloadDialog { DataContext = new DownloadDialogViewModel(vm(), initial) }.ShowDialog(owner);
    }

    public async Task<string?> ReadClipboardTextAsync() =>
        owner.Clipboard is { } c ? await c.TryGetTextAsync() : null;

    public Task ShowSettingsAsync() => new SettingsWindow { DataContext = vm() }.ShowDialog(owner);

    public async Task CopyToClipboardAsync(string text)
    {
        if (owner.Clipboard is { } c) await c.SetTextAsync(text);
    }

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        var (exe, args) = OperatingSystem.IsWindows() ? ("explorer.exe", $"\"{path}\"")
            : OperatingSystem.IsMacOS() ? ("open", $"\"{path}\"")
            : ("xdg-open", $"\"{path}\"");
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
