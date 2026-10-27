using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paramaja.Core.Input;

namespace Paramaja.App.ViewModels;

/// <summary>A resolution to download; Height 0 is "best available".</summary>
public sealed record QualityOption(int Height, string Label)
{
    public override string ToString() => Label;

    public static QualityOption For(int height) => new(height, height switch
    {
        0 => "Best available",
        >= 4320 => $"{height}p (8K)",
        >= 2160 => $"{height}p (4K)",
        >= 1440 => $"{height}p (2K)",
        >= 1080 => $"{height}p (Full HD)",
        >= 720 => $"{height}p (HD)",
        _ => $"{height}p",
    });
}

/// <summary>
/// Paste a link, see which resolutions it comes in, pick one: the best audio is merged in.
/// The download itself runs in the main view model, so it carries on if the window closes.
/// </summary>
public sealed partial class DownloadDialogViewModel : ObservableObject
{
    public MainViewModel Main { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private string _url = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(StartCommand))]
    private bool _isChecking;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private QualityOption? _selectedQuality;

    public ObservableCollection<QualityOption> Qualities { get; } = [];

    public DownloadDialogViewModel(MainViewModel main, string? initial = null)
    {
        Main = main;
        if (IsWebLink(initial)) _url = initial!.Trim();
        Main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsDownloading)) StartCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Downloads come from web pages (yt-dlp); magnets and MEGA links don't apply.</summary>
    public bool IsValid => IsWebLink(Url);

    private static bool IsWebLink(string? text) => InputClassifier.Classify(text).Kind == InputKind.Url;

    partial void OnUrlChanged(string value)
    {
        Qualities.Clear();
        SelectedQuality = null;
        Title = "";
        Message = "";
    }

    private bool CanCheck() => IsValid && !IsChecking;

    /// <summary>Looks up the resolutions on offer.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task Check()
    {
        IsChecking = true;
        Message = "Looking up the available qualities…";
        try
        {
            var info = await Task.Run(() => Main.YtDlp.GetInfoAsync(Url.Trim()));
            Qualities.Clear();
            Qualities.Add(QualityOption.For(0));
            foreach (var h in info.Heights) Qualities.Add(QualityOption.For(h));
            SelectedQuality = Qualities[0];
            Title = info.Title;
            Message = info.Heights.Count == 0 ? "No separate qualities listed: the best available will be downloaded." : "";
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException
                                      or System.Text.Json.JsonException)
        {
            Message = $"Couldn't read that link: {e.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    private bool CanStart() => SelectedQuality is not null && !IsChecking && !Main.IsDownloading;

    /// <summary>Starts the download; progress shows here and at the top of the player.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => _ = Main.StartDownloadAsync(Url.Trim(), SelectedQuality!.Height);
}
