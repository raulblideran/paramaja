using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paramaja.Core.Settings;

namespace Paramaja.App.ViewModels;

/// <summary>A button (or group) of the controls bar, shown or hidden in Settings → Interface.</summary>
public sealed partial class ControlChoice(string id, string label, bool shown, Action<ControlChoice> changed) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isShown = shown;
    partial void OnIsShownChanged(bool value) => changed(this);
}

/// <summary>
/// The controls bar: which of its buttons show (each where it applies, and where the user hasn't
/// hidden it), and Previous / Next through a playlist: a torrent's files, or a folder's.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The labels of <see cref="PlayerSettings.ControlIds"/>, as Settings → Interface lists them.</summary>
    public static readonly IReadOnlyDictionary<string, string> ControlLabels = new Dictionary<string, string>
    {
        ["files"] = "Previous / Next file (folders, several files, torrents)",
        ["frames"] = "Previous / Next frame",
        ["stop"] = "Stop",
        ["chapters"] = "Previous / Next chapter",
        ["volume"] = "Mute and volume",
        ["badges"] = "Badges (HDR, codec, resolution, decoder, audio, deinterlacing)",
        ["discmenu"] = "Disc menu (DVDs, Blu-rays)",
        ["chaptermenu"] = "Titles & chapters",
        ["subtitles"] = "Subtitles",
        ["audio"] = "Audio",
        ["video"] = "Video",
        ["screenshot"] = "Screenshot",
        ["download"] = "Download Video",
        ["openlink"] = "Open Link",
        ["open"] = "Open",
        ["fullscreen"] = "Fullscreen",
    };

    private ObservableCollection<ControlChoice>? _controlChoices;

    /// <summary>One switch per button of the bar (Play and Settings always show).</summary>
    public ObservableCollection<ControlChoice> ControlChoices => _controlChoices ??= new(PlayerSettings.ControlIds
        .Select(id => new ControlChoice(id, ControlLabels[id], Shows(id), OnControlChoice)));

    private bool Shows(string id) => !Settings.HiddenControls.Contains(id);

    private void OnControlChoice(ControlChoice choice)
    {
        if (choice.IsShown == Shows(choice.Id)) return;
        if (choice.IsShown) Settings.HiddenControls.Remove(choice.Id);
        else Settings.HiddenControls.Add(choice.Id);
        OnControlsShownChanged();
        SaveSettings();
    }

    /// <summary>Every button back on the bar.</summary>
    [RelayCommand]
    private void ShowAllControls()
    {
        Settings.HiddenControls.Clear();
        foreach (var c in ControlChoices) c.IsShown = true;
        OnControlsShownChanged();
        SaveSettings();
    }

    private void OnControlsShownChanged()
    {
        foreach (var name in ShowNames) OnPropertyChanged(name);
    }

    private static readonly string[] ShowNames =
    [
        nameof(ShowFileButtons), nameof(ShowFrameButtons), nameof(ShowStopButton), nameof(ShowChapterButtons), nameof(ShowVolume),
        nameof(ShowBadges), nameof(ShowDiscMenuButton), nameof(ShowChapterMenuButton), nameof(ShowSubtitlesButton),
        nameof(ShowAudioButton), nameof(ShowVideoButton), nameof(ShowScreenshotButton), nameof(ShowDownloadButton),
        nameof(ShowOpenLinkButton), nameof(ShowOpenButton), nameof(ShowFullscreenButton),
    ];

    public bool ShowFileButtons => Shows("files") && HasPlaylist;
    public bool ShowFrameButtons => Shows("frames") && !IsAudioOnly;
    public bool ShowStopButton => Shows("stop");
    public bool ShowChapterButtons => Shows("chapters") && HasChapters;
    public bool ShowVolume => Shows("volume");
    public bool ShowBadges => Shows("badges");
    public bool ShowDiscMenuButton => Shows("discmenu") && CanShowDiscMenu;
    public bool ShowChapterMenuButton => Shows("chaptermenu") && HasChapterMenu;
    public bool ShowSubtitlesButton => Shows("subtitles");
    public bool ShowAudioButton => Shows("audio");
    public bool ShowVideoButton => Shows("video") && !IsAudioOnly;
    public bool ShowScreenshotButton => Shows("screenshot");
    public bool ShowDownloadButton => Shows("download");
    public bool ShowOpenLinkButton => Shows("openlink");
    public bool ShowOpenButton => Shows("open");
    public bool ShowFullscreenButton => Shows("fullscreen");

    // ---- Previous / Next file -----------------------------------------------------------------

    /// <summary>
    /// Files to go back and forth through: a torrent's, or mpv's playlist (a folder's files, or
    /// several dropped at once). A disc's titles and chapters have their own buttons.
    /// </summary>
    public bool HasPlaylist => HasTorrentFiles || (!IsIdle && !IsDiscMode && (_player?.State.PlaylistCount ?? 0) > 1);

    public string PreviousFileTip => IsTorrent ? "Previous file in this torrent" : "Previous file";
    public string NextFileTip => IsTorrent ? "Next file in this torrent" : "Next file";

    [RelayCommand]
    private void PreviousFile()
    {
        if (IsTorrent) StepTorrentFile(-1);
        else _player?.PlaylistStep(-1);
    }

    [RelayCommand]
    private void NextFile()
    {
        if (IsTorrent) StepTorrentFile(1);
        else _player?.PlaylistStep(1);
    }

    /// <summary>What else changes with a property: the bar's buttons go with what they need.</summary>
    private static readonly Dictionary<string, string[]> Dependents = new()
    {
        [nameof(HasTorrentFiles)] = [nameof(HasPlaylist)],
        [nameof(IsIdle)] = [nameof(HasPlaylist)],
        [nameof(IsDiscMode)] = [nameof(HasPlaylist)],
        [nameof(IsTorrent)] = [nameof(PreviousFileTip), nameof(NextFileTip)],
        [nameof(HasPlaylist)] = [nameof(ShowFileButtons)],
        [nameof(IsAudioOnly)] = [nameof(ShowFrameButtons), nameof(ShowVideoButton)],
        [nameof(HasChapters)] = [nameof(ShowChapterButtons)],
        [nameof(CanShowDiscMenu)] = [nameof(ShowDiscMenuButton)],
        [nameof(HasChapterMenu)] = [nameof(ShowChapterMenuButton)],
    };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && Dependents.TryGetValue(name, out var more))
            foreach (var m in more) OnPropertyChanged(m);
    }
}
