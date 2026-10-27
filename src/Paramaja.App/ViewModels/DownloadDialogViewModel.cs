using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paramaja.Core.Input;

namespace Paramaja.App.ViewModels;

/// <summary>A resolution to download; Height 0 is "best available". <see cref="AudioOnly"/> is the audio alone.</summary>
public sealed record QualityOption(int Height, string Label, bool AudioOnly = false)
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

    /// <summary>The best audio stream on its own, kept as it is (no re-encoding).</summary>
    public static QualityOption Audio { get; } = new(0, "Audio only (best quality)", AudioOnly: true);
}

/// <summary>
/// Paste a link and its resolutions load by themselves; pick one (the best audio is merged in) or
/// the audio alone. The download itself runs in the main view model, so it carries on if the
/// window closes.
/// </summary>
public sealed partial class DownloadDialogViewModel : ObservableObject
{
    public MainViewModel Main { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string _url = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isChecking;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(WithBestAudio))]
    private QualityOption? _selectedQuality;

    public ObservableCollection<QualityOption> Qualities { get; } = [];

    /// <summary>A video quality is chosen, which gets the best audio merged in.</summary>
    public bool WithBestAudio => SelectedQuality is { AudioOnly: false };

    /// <summary>How long typing has to pause before a link is looked up (a paste is one change).</summary>
    public TimeSpan LookupDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>The lookup in progress or last finished, for tests to await.</summary>
    internal Task Lookup { get; private set; } = Task.CompletedTask;

    private CancellationTokenSource? _lookupCts;

    public DownloadDialogViewModel(MainViewModel main, string? initial = null)
    {
        Main = main;
        Main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsDownloading)) StartCommand.NotifyCanExecuteChanged();
        };
        // Opened with a link already in the box: its qualities load straight away.
        if (IsWebLink(initial))
        {
            _url = initial!.Trim();
            StartLookup(TimeSpan.Zero);
        }
    }

    /// <summary>Downloads come from web pages (yt-dlp); magnets and MEGA links don't apply.</summary>
    public bool IsValid => IsWebLink(Url);

    private static bool IsWebLink(string? text) => InputClassifier.Classify(text).Kind == InputKind.Url;

    partial void OnUrlChanged(string value)
    {
        _lookupCts?.Cancel();
        _lookupCts = null;
        Qualities.Clear();
        SelectedQuality = null;
        Title = "";
        Message = "";
        IsChecking = false;
        if (IsValid) StartLookup(LookupDelay);
    }

    private void StartLookup(TimeSpan delay)
    {
        var cts = _lookupCts = new CancellationTokenSource();
        Lookup = LookUpAsync(Url.Trim(), delay, cts.Token);
    }

    /// <summary>Looks up the resolutions on offer, once typing has paused; a newer link cancels it.</summary>
    private async Task LookUpAsync(string url, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            IsChecking = true;
            Message = "Looking up the available qualities…";
            var info = await Task.Run(() => Main.YtDlp.GetInfoAsync(url, ct), ct);
            if (ct.IsCancellationRequested) return;
            Qualities.Clear();
            Qualities.Add(QualityOption.For(0));
            foreach (var h in info.Heights) Qualities.Add(QualityOption.For(h));
            Qualities.Add(QualityOption.Audio);
            SelectedQuality = Qualities[0];
            Title = info.Title;
            Message = info.Heights.Count == 0 ? "No separate qualities listed: the best available will be downloaded." : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException
                                      or System.Text.Json.JsonException)
        {
            if (!ct.IsCancellationRequested) Message = $"Couldn't read that link: {e.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested) IsChecking = false;
        }
    }

    private bool CanStart() => SelectedQuality is not null && !IsChecking && !Main.IsDownloading;

    /// <summary>Starts the download; progress shows here and at the top of the player.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() =>
        _ = Main.StartDownloadAsync(Url.Trim(), SelectedQuality!.Height, SelectedQuality.AudioOnly);
}
