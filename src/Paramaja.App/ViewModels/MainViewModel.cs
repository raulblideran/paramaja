using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Paramaja.App.Services;
using Paramaja.Core.Input;
using Paramaja.Core.Mpv;
using Paramaja.Core.Playback;
using Paramaja.Core.Settings;
using Paramaja.Core.Torrent;

namespace Paramaja.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly PortablePaths _paths;
    private readonly Func<long?, IPlayer> _playerFactory;
    private readonly ITorrentService _torrents;
    private readonly IUiDispatcher _ui;
    private IPlayer? _player;
    private string? _pendingOpen;
    private TorrentInfo? _torrent;
    private DateTime _lastActivity = DateTime.UtcNow;
    private DateTime _lastQuip = DateTime.MinValue;
    private (int X, int Y)? _lastPointer;
    private readonly YtDlp _ytdlp;
    private readonly PlaybackHealth _health = new();
    private MediaSource _source = MediaSource.None;
    [ObservableProperty] private string _renderProfileText = "";

    public PlayerSettings Settings { get; }
    public IDialogService? Dialogs { get; set; }
    public IPlayer? Player => _player;

    /// <summary>Raised when the view should toggle (true/false) or leave (false) fullscreen.</summary>
    public event Action<bool?>? FullscreenRequested;

    public MainViewModel(PlayerSettings settings, SettingsStore store, PortablePaths paths,
        Func<long?, IPlayer> playerFactory, ITorrentService torrents, IUiDispatcher ui, YtDlp? ytdlp = null)
    {
        _ytdlp = ytdlp ?? new YtDlp(AppContext.BaseDirectory);
        Settings = settings;
        _store = store;
        _paths = paths;
        _playerFactory = playerFactory;
        _torrents = torrents;
        _ui = ui;
        RecentItems = new ObservableCollection<string>(settings.RecentItems);
    }

    // ---- Observable state -----------------------------------------------------------------

    [ObservableProperty] private bool _isIdle = true;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isFullscreen;
    [ObservableProperty] private bool _controlsVisible = true;
    [ObservableProperty] private bool _isPointerOverControls;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _bufferedUntil;
    [ObservableProperty] private double _volume = 100;
    [ObservableProperty] private string _mediaTitle = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _hdrBadge = "";
    [ObservableProperty] private string _decoderBadge = "";
    [ObservableProperty] private string _resolutionBadge = "";
    [ObservableProperty] private string _audioBadge = "";
    [ObservableProperty] private bool _isTorrent;
    [ObservableProperty] private bool _isResolving;
    [ObservableProperty] private string _bufferingText = "";
    [ObservableProperty] private string _deinterlaceBadge = "";
    [ObservableProperty] private string _ytDlpStatus = "";
    [ObservableProperty] private string _torrentStatus = "";
    [ObservableProperty] private TorrentFileItem? _selectedTorrentFile;
    /// <summary>Set by the view: the window's display is in HDR mode (only detectable on Windows).</summary>
    [ObservableProperty] private bool _displayIsHdr;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanDownload))] private bool _isDownloading;
    [ObservableProperty] private string _downloadStatus = "";

    public string PositionText => TimeFormat.Format(Position, Duration >= 3600);
    public string DurationText => TimeFormat.Format(Duration);
    public string WindowTitle => string.IsNullOrEmpty(MediaTitle) ? "Paramaja" : $"{MediaTitle} — Paramaja";
    public bool HasTorrentFiles => TorrentFiles.Count > 1;
    public string VolumeText => $"{Volume:0}%";
    /// <summary>The slider is logarithmic; the tooltip says what it means in decibels.</summary>
    public string VolumeDecibels => VolumeCurve.Describe(Volume);
    public bool HasYtDlp => _ytdlp.BundledPath is not null;
    public string AppVersion => AppInfo.Display;
    public string AppVersionLine => $"{AppInfo.Name} {AppInfo.Display}";

    public ObservableCollection<TrackItem> AudioTracks { get; } = [];
    public ObservableCollection<TrackItem> SubtitleTracks { get; } = [];
    public ObservableCollection<TorrentFileItem> TorrentFiles { get; } = [];
    public ObservableCollection<ChapterItem> Chapters { get; } = [];
    /// <summary>A DVD's or Blu-ray's titles (main film, extras); empty for ordinary files.</summary>
    public ObservableCollection<ChapterItem> DiscTitles { get; } = [];
    public bool HasChapters => Chapters.Count > 1;
    public bool HasDiscTitles => DiscTitles.Count > 1;
    public bool HasChapterMenu => HasChapters || HasDiscTitles;
    public ObservableCollection<string> RecentItems { get; }
    public IReadOnlyList<string> ToneMappingCurves => PlayerSettings.ToneMappingCurves;
    public IReadOnlyList<(string Keys, PlayerAction Action)> Shortcuts { get; } = ShortcutMap.Describe().ToList();

    partial void OnPositionChanged(double value) => OnPropertyChanged(nameof(PositionText));
    partial void OnDurationChanged(double value)
    {
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(PositionText));
    }
    partial void OnMediaTitleChanged(string value) => OnPropertyChanged(nameof(WindowTitle));
    partial void OnDisplayIsHdrChanged(bool value)
    {
        _player?.SetDisplayHdr(value);
        OnHdrEffectChanged();
        UpdateHdrBadge();
    }

    // ---- Player lifecycle -----------------------------------------------------------------

    /// <summary>Called once the video surface exists (its native handle is mpv's render target).</summary>
    public void AttachPlayer(long? windowId)
    {
        if (_player is not null) return;
        _player = _playerFactory(windowId);
        _player.StateChanged += name => _ui.Post(() => SyncState(name));
        _player.TracksChanged += () => _ui.Post(SyncTracks);
        _player.ChaptersChanged += () => _ui.Post(SyncChapters);
        _player.KeyPressed += key => _ui.Post(() => HandleKey(key));
        _player.MouseMoved += () => _ui.Post(NotifyActivity);
        _player.MouseClicked += () => _ui.Post(() => HandleClick(DateTime.UtcNow));
        _player.FileLoaded += () => _ui.Post(_health.Reset);
        _player.RenderProfileChanged += p => _ui.Post(() => RenderProfileText = DescribeProfile(p));
        RenderProfileText = DescribeProfile(_player.RenderProfile);
        if (DisplayIsHdr) _player.SetDisplayHdr(true);
        _player.EndFile += (reason, error) => _ui.Post(() =>
        {
            if (reason == EndFileReason.Error) ShowStatus($"Can't play this: {error}");
        });
        if (_pendingOpen is { } p)
        {
            _pendingOpen = null;
            _ = OpenInputAsync(p);
        }
    }

    private void SyncState(string name)
    {
        if (_player is null) return;
        var s = _player.State;
        switch (name)
        {
            case "time-pos": Position = s.Position; UpdateBuffered(s); break;
            case "duration": Duration = s.Duration; break;
            case "pause": IsPaused = s.IsPaused; if (IsPaused) NotifyActivity(); break;
            case "mute": IsMuted = s.IsMuted; break;
            case "volume": Volume = s.Volume; break;
            case "deinterlace-active": DeinterlaceBadge = s.DeinterlaceActive ? s.DeinterlaceFilter ?? "" : ""; break;
            case "paused-for-cache": IsBuffering = s.IsBuffering; break;
            case "idle-active": IsIdle = s.IsIdle; OnPropertyChanged(nameof(CanDownload)); break;
            case "media-title": MediaTitle = s.Title ?? ""; break;
            case "demuxer-cache-time": UpdateBuffered(s); break;
            case "hwdec-current": DecoderBadge = s.IsHardwareDecoding ? $"HW · {s.HwdecCurrent}" : "SW"; break;
            case "video-params/gamma" or "video-params/primaries": UpdateHdrBadge(); break;
            case "video-format": OnPropertyChanged(nameof(IsHevcVideo)); break;
            case "chapter": MarkCurrentChapter(); break;
            case "video-params/h" or "video-params/w": ResolutionBadge = ResolutionLabel(s.VideoWidth, s.VideoHeight); break;
            case "audio-params/channel-count" or "audio-out-params/channel-count":
                UpdateAudioBadge();
                OnPropertyChanged(nameof(IsSurroundAudio));
                break;
        }
    }

    private void UpdateBuffered(PlayerState s) =>
        BufferedUntil = s.CacheTime is { } c ? Math.Min(Duration, Math.Max(c, s.Position)) : s.Position;

    /// <summary>The playing video is HDR: only then does the HDR / tonemapping menu apply.</summary>
    public bool IsHdrVideo => _player?.State.IsHdr == true;

    /// <summary>5.1, 7.1 and the like: only then is there anything to downmix.</summary>
    public bool IsSurroundAudio => _player?.State.SourceChannels > 2;

    /// <summary>Hardware decoding is used for HEVC only, so its switch only matters for HEVC.</summary>
    public bool IsHevcVideo => _player?.State.VideoFormat == "hevc";

    internal void UpdateHdrBadge()
    {
        OnPropertyChanged(nameof(IsHdrVideo));
        var s = _player?.State;
        if (s is null || !s.IsHdr) { HdrBadge = ""; return; }
        var kind = s.Transfer == "hlg" ? "HLG" : "HDR";
        HdrBadge = HdrPassthrough ? $"{kind} passthrough" : $"{kind} → SDR";
    }

    internal void UpdateAudioBadge()
    {
        var s = _player?.State;
        if (s?.SourceChannels is not { } src) { AudioBadge = ""; return; }
        var label = TrackInfo.ChannelLabel(src);
        AudioBadge = s.OutputChannels is { } o && o != src ? $"{label} → {TrackInfo.ChannelLabel(o)}" : label;
    }

    internal static string ResolutionLabel(int? w, int? h) => (w, h) switch
    {
        (null, _) or (_, null) => "",
        ( >= 3800, _) or (_, >= 2100) => "4K",
        ( >= 2500, _) or (_, >= 1400) => "1440p",
        ( >= 1900, _) or (_, >= 1000) => "1080p",
        ( >= 1260, _) or (_, >= 700) => "720p",
        _ => $"{h}p",
    };

    private void SyncTracks()
    {
        if (_player is null) return;
        var tracks = _player.Tracks;
        Replace(AudioTracks, tracks.Where(t => t.Type == TrackType.Audio).Select(t => new TrackItem(t, t.Label)));
        var subs = tracks.Where(t => t.Type == TrackType.Subtitle).Select(t => new TrackItem(t, t.Label)).ToList();
        subs.Insert(0, TrackItem.Off(!subs.Any(s => s.IsSelected)));
        Replace(SubtitleTracks, subs);
    }

    private void SyncChapters()
    {
        if (_player is null) return;
        Replace(Chapters, _player.Chapters.Select(c => new ChapterItem(c.Index, c.Label)));
        Replace(DiscTitles, _player.Titles.Select(t => new ChapterItem(t.Id, t.Title) { IsCurrent = t.Selected }));
        MarkCurrentChapter();
        OnPropertyChanged(nameof(HasChapters));
        OnPropertyChanged(nameof(HasDiscTitles));
        OnPropertyChanged(nameof(HasChapterMenu));
    }

    private void MarkCurrentChapter()
    {
        var current = _player?.State.Chapter;
        foreach (var c in Chapters) c.IsCurrent = c.Id == current;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    // ---- Opening media --------------------------------------------------------------------

    /// <summary>Opens a file path, URL or magnet link (from drop, paste, command line or dialog).</summary>
    public async Task OpenInputAsync(string? input)
    {
        var (kind, value) = InputClassifier.Classify(input);
        if (kind == InputKind.None)
        {
            if (!string.IsNullOrWhiteSpace(input)) ShowStatus("That isn't a file, link or magnet I can open.");
            return;
        }
        if (_player is null)
        {
            _pendingOpen = value;
            return;
        }
        switch (kind)
        {
            case InputKind.Magnet:
                await OpenMagnetAsync(value);
                return;
            case InputKind.Mega:
                await OpenMegaAsync(value);
                return;
        }
        CloseTorrent();
        SetSource(kind is InputKind.Url ? MediaSource.Web : MediaSource.Local, kind is InputKind.Url ? value : null);
        if (kind is InputKind.Dvd or InputKind.Bluray)
        {
            try { _player.OpenDisc(value, bluray: kind == InputKind.Bluray); }
            catch (MpvException)
            {
                ShowStatus(kind == InputKind.Dvd ? "This build can't play DVD folders." : "This build can't play Blu-ray folders.");
                return;
            }
        }
        else _player.Open(value, streaming: InputClassifier.IsStreaming(kind));
        Remember(value);
    }

    private async Task OpenMagnetAsync(string magnet)
    {
        if (IsResolving) return;
        try
        {
            IsResolving = true;
            ShowStatus("Finding peers and fetching torrent info…", sticky: true);
            EnsureEngine();
            var info = await _torrents.AddAsync(magnet, TimeSpan.FromSeconds(Settings.TorrentMetadataTimeoutSeconds));
            if (_torrent is { } previous && previous.Id != info.Id) CloseTorrent();
            _torrent = info;
            IsTorrent = true;

            Replace(TorrentFiles, info.Files
                .Where(f => f.IsVideo)
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Select(f => new TorrentFileItem(f.Index, f.FileName, TimeFormat.Bytes(f.Length))));
            OnPropertyChanged(nameof(HasTorrentFiles));
            _selectingTorrentFile = true;
            SelectedTorrentFile = TorrentFiles.FirstOrDefault(f => f.Index == info.SelectedFile);
            _selectingTorrentFile = false;

            if (info.StreamUrl is null || info.SelectedFile is not { } fileIdx)
            {
                ShowStatus("This torrent has no playable files.");
                return;
            }
            var file = info.Files.First(f => f.Index == fileIdx);
            SetSource(MediaSource.Torrent);
            _player!.Open(info.StreamUrl, file.FileName, streaming: true);
            Remember(magnet);
            ShowStatus($"Streaming {file.FileName}");
        }
        catch (TorrentException e)
        {
            ShowStatus($"Torrent failed: {e.Message}");
        }
        finally
        {
            IsResolving = false;
        }
    }

    /// <summary>The engine serves both torrents and MEGA; it starts on first use.</summary>
    private void EnsureEngine()
    {
        if (_torrents.IsStarted) return;
        // Leftovers from a crash or a killed process would otherwise pile up.
        if (Settings.ClearTorrentCacheOnExit) ClearTorrentCache();
        _torrents.Start(new TorrentEngineConfig
        {
            CacheDir = _paths.TorrentCache,
            ListenPort = Settings.TorrentListenPort,
            EnableUpnp = Settings.TorrentUpnp,
        });
    }

    private async Task OpenMegaAsync(string link)
    {
        if (IsResolving) return;
        try
        {
            IsResolving = true;
            ShowStatus("Fetching the file from MEGA…", sticky: true);
            EnsureEngine();
            var info = await _torrents.AddMegaAsync(link, TimeSpan.FromSeconds(30));
            CloseTorrent();
            SetSource(MediaSource.Mega);
            _player!.Open(info.StreamUrl, info.Name, streaming: true);
            Remember(link);
            ShowStatus($"Streaming {info.Name} from MEGA");
        }
        catch (TorrentException e)
        {
            ShowStatus($"MEGA: {e.Message}");
        }
        finally
        {
            IsResolving = false;
        }
    }

    private bool _selectingTorrentFile;

    partial void OnSelectedTorrentFileChanged(TorrentFileItem? value)
    {
        if (_selectingTorrentFile || value is null || _torrent is null || _player is null) return;
        try
        {
            var url = _torrents.SelectFile(_torrent.Id, value.Index);
            _player.Open(url, value.Name, streaming: true);
        }
        catch (TorrentException e)
        {
            ShowStatus($"Torrent failed: {e.Message}");
        }
    }

    private void CloseTorrent()
    {
        if (_torrent is null) return;
        try { _torrents.Remove(_torrent.Id, deleteFiles: Settings.ClearTorrentCacheOnExit); }
        catch (TorrentException) { }
        _torrent = null;
        IsTorrent = false;
        TorrentStatus = "";
        TorrentFiles.Clear();
        OnPropertyChanged(nameof(HasTorrentFiles));
    }

    /// <summary>Polled by the view about once a second while a torrent is active.</summary>
    public void RefreshTorrentStats()
    {
        if (_torrent is null) return;
        try
        {
            var s = _torrents.GetStats(_torrent.Id);
            var file = SelectedTorrentFile is { } f && f.Index < s.FileProgress.Count ? s.FileProgress[f.Index] : s.ProgressBytes;
            var len = _torrent.Files.FirstOrDefault(x => x.Index == SelectedTorrentFile?.Index)?.Length ?? s.TotalBytes;
            var pct = len > 0 ? 100.0 * file / len : 0;
            TorrentStatus = s.Error is { } err
                ? $"Torrent error: {err}"
                : $"↓ {s.DownloadMiBps:0.0} MiB/s · {s.PeersLive} peers · {pct:0}% downloaded";
        }
        catch (TorrentException) { }
    }

    private void Remember(string item)
    {
        Settings.AddRecent(item);
        Replace(RecentItems, Settings.RecentItems);
        SaveSettings();
    }

    private string? _webUrl;

    private void SetSource(MediaSource source, string? webUrl = null)
    {
        _source = source;
        _webUrl = webUrl;
        OnPropertyChanged(nameof(CanDownload));
    }

    // ---- Downloads ------------------------------------------------------------------------

    private CancellationTokenSource? _downloadCts;
    private Task? _downloadTask;

    /// <summary>The playing video came from a web page yt-dlp can save (YouTube and the like).</summary>
    public bool CanDownload => _webUrl is not null && HasYtDlp && !IsDownloading && !IsIdle;

    /// <summary>Saves the playing web video, best quality, to the Downloads folder.</summary>
    [RelayCommand]
    private async Task Download()
    {
        if (_webUrl is not { } url || IsDownloading) return;
        using var cts = _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadStatus = "Starting download…";
        try
        {
            var task = Task.Run(() => _ytdlp.DownloadAsync(url, _paths.Downloads,
                p => _ui.Post(() => { if (IsDownloading) DownloadStatus = p.ToString(); }), cts.Token));
            _downloadTask = task;
            var file = await task;
            ShowStatus($"Saved to {Path.GetFileName(_paths.Downloads.TrimEnd(Path.DirectorySeparatorChar))}: {Path.GetFileName(file)}");
        }
        catch (OperationCanceledException) { ShowStatus("Download cancelled."); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            ShowStatus($"Download failed: {e.Message}");
        }
        finally
        {
            _downloadCts = null;
            IsDownloading = false;
            DownloadStatus = "";
        }
    }

    [RelayCommand] private void CancelDownload() => _downloadCts?.Cancel();

    [RelayCommand] private void OpenDownloadsFolder() => Dialogs?.OpenFolder(_paths.Downloads);

    // ---- Commands -------------------------------------------------------------------------

    [RelayCommand] private void TogglePause() => _player?.TogglePause();
    [RelayCommand] private void FrameStep() => _player?.FrameStep();
    [RelayCommand] private void FrameBackStep() => _player?.FrameBackStep();
    [RelayCommand] private void ToggleMute() => _player?.ToggleMute();
    [RelayCommand] private void NextChapter() => _player?.StepChapter(1);
    [RelayCommand] private void PreviousChapter() => _player?.StepChapter(-1);
    [RelayCommand] private void GoToChapter(ChapterItem item) => _player?.GoToChapter(item.Id);
    [RelayCommand] private void SelectDiscTitle(ChapterItem item) => _player?.SelectTitle(item.Id);
    [RelayCommand] private void ToggleStats() => _player?.ToggleStats();
    [RelayCommand] private void ToggleFullscreen() => FullscreenRequested?.Invoke(null);

    [RelayCommand]
    private void Screenshot(bool withSubtitles)
    {
        if (_player is null || IsIdle) return;
        try
        {
            var file = _player.Screenshot(withSubtitles);
            _player.ShowText($"Screenshot saved: {Path.GetFileName(file)}");
            ShowStatus($"Saved {Path.GetFileName(file)}");
        }
        catch (MpvException e)
        {
            ShowStatus($"Screenshot failed: {e.Message}");
        }
    }

    [RelayCommand] private void OpenScreenshotsFolder() => Dialogs?.OpenFolder(_paths.Screenshots);

    [RelayCommand]
    private async Task OpenFile()
    {
        if (Dialogs is null) return;
        if (await Dialogs.PickMediaFileAsync() is { } f) await OpenInputAsync(f);
    }

    [RelayCommand]
    private async Task OpenFolder()
    {
        if (Dialogs is null) return;
        if (await Dialogs.PickFolderAsync() is { } f) await OpenInputAsync(f);
    }

    [RelayCommand]
    private async Task OpenLink()
    {
        if (Dialogs is null) return;
        if (await Dialogs.AskLinkAsync() is { } m) await OpenInputAsync(m);
    }

    [RelayCommand]
    private async Task PasteAndPlay()
    {
        if (Dialogs is null) return;
        var text = await Dialogs.ReadClipboardTextAsync();
        if (string.IsNullOrWhiteSpace(text)) ShowStatus("Clipboard is empty.");
        else await OpenInputAsync(text);
    }

    [RelayCommand] private Task OpenRecent(string item) => OpenInputAsync(item);

    [RelayCommand]
    private async Task LoadSubtitle()
    {
        if (Dialogs is null || _player is null) return;
        if (await Dialogs.PickSubtitleFileAsync() is { } f)
        {
            try { _player.AddSubtitle(f); }
            catch (MpvException e) { ShowStatus($"Couldn't load subtitles: {e.Message}"); }
        }
    }

    [RelayCommand]
    private void SelectAudio(TrackItem item)
    {
        if (item.Track is { } t) _player?.SelectTrack(TrackType.Audio, t.Id);
    }

    [RelayCommand] private void SelectSubtitle(TrackItem item) => _player?.SelectTrack(TrackType.Subtitle, item.Track?.Id);

    [RelayCommand]
    private async Task OpenSettings()
    {
        if (Dialogs is not null) await Dialogs.ShowSettingsAsync();
        SaveSettings();
    }

    [RelayCommand] private void OpenCacheFolder() => Dialogs?.OpenFolder(_paths.TorrentCache);

    internal string DescribeProfile(RenderProfile p) =>
        Settings.RenderQuality == RenderQuality.Auto ? $"Auto is using {p}" : $"Using {p}";

    /// <summary>
    /// Fed about once a second by the view. With Auto rendering, sustained frame drops
    /// switch to the Fast profile for the rest of the session.
    /// </summary>
    public void RefreshHealth()
    {
        if (_player is null || IsIdle) return;
        _health.Add(_player.SampleHealth());
        if (Settings.RenderQuality == RenderQuality.Auto && _player.RenderProfile != RenderProfile.Fast && _health.RenderingStruggling)
        {
            _player.FallBackToFastRendering();
            _health.Reset(); // judge the new profile on its own frames
            const string msg = "Frames were dropping, so Paramaja switched to Fast rendering (Settings → Video → Rendering)";
            _player.ShowText(msg, 5000);
            ShowStatus(msg);
        }
    }

    public string BuildDiagnostics() => _player is null
        ? $"{AppInfo.Name} {AppInfo.Display}\nNothing is playing."
        : DiagnosticsReport.Build(_player.State, Settings, _player.RenderProfile, _health, _source, TorrentStatus);

    /// <summary>Copies a report to paste to whoever is helping; play the problem video first.</summary>
    [RelayCommand]
    private async Task CopyDiagnostics()
    {
        if (Dialogs is null) return;
        await Dialogs.CopyToClipboardAsync(BuildDiagnostics());
        ShowStatus("Diagnostics copied: paste them to whoever is helping you.");
        _player?.ShowText("Diagnostics copied to the clipboard");
    }

    /// <summary>Sites (YouTube especially) change often; a yt-dlp update fixes most failures.</summary>
    [RelayCommand]
    private async Task UpdateYtDlp()
    {
        if (!HasYtDlp) { YtDlpStatus = "yt-dlp isn't bundled with this build."; return; }
        YtDlpStatus = "Updating yt-dlp…";
        try { YtDlpStatus = await _ytdlp.UpdateAsync(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            YtDlpStatus = $"Update failed: {e.Message}";
        }
    }

    /// <summary>Seek bar: jump to an absolute time.</summary>
    public void SeekTo(double seconds) => _player?.SeekAbsolute(seconds);

    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(VolumeDecibels));
        if (_player is not null && Math.Abs(_player.State.Volume - value) > 0.01) _player.SetVolume(value);
    }

    // ---- Keyboard -------------------------------------------------------------------------

    /// <summary>Handles a canonical key name (from the app window or forwarded by mpv).</summary>
    public bool HandleKey(string key)
    {
        if (!ShortcutMap.TryGet(key, out var action)) return false;
        NotifyActivity();
        Execute(action);
        return true;
    }

    public void Execute(PlayerAction action)
    {
        var p = _player;
        switch (action)
        {
            case PlayerAction.TogglePause: p?.TogglePause(); break;
            case PlayerAction.SeekForward: p?.SeekRelative(Settings.SeekSeconds); break;
            case PlayerAction.SeekBackward: p?.SeekRelative(-Settings.SeekSeconds); break;
            case PlayerAction.SeekForwardLong: p?.SeekRelative(Settings.LongSeekSeconds); break;
            case PlayerAction.SeekBackwardLong: p?.SeekRelative(-Settings.LongSeekSeconds); break;
            case PlayerAction.FrameStep: p?.FrameStep(); break;
            case PlayerAction.FrameBackStep: p?.FrameBackStep(); break;
            case PlayerAction.NextChapter: p?.StepChapter(1); break;
            case PlayerAction.PreviousChapter: p?.StepChapter(-1); break;
            case PlayerAction.VolumeUp: p?.ChangeVolume(5); Osd($"Volume {p?.Settings.Volume:0}%"); break;
            case PlayerAction.VolumeDown: p?.ChangeVolume(-5); Osd($"Volume {p?.Settings.Volume:0}%"); break;
            case PlayerAction.ToggleMute: p?.ToggleMute(); break;
            case PlayerAction.ToggleFullscreen: FullscreenRequested?.Invoke(null); break;
            case PlayerAction.ExitFullscreen: FullscreenRequested?.Invoke(false); break;
            case PlayerAction.Screenshot: Screenshot(false); break;
            case PlayerAction.ScreenshotWithSubtitles: Screenshot(true); break;
            case PlayerAction.ToggleDeinterlace: CycleDeinterlace(); break;
            case PlayerAction.ToggleStats: p?.ToggleStats(); break;
            case PlayerAction.ToggleHdrMode:
                HdrPassthrough = !HdrPassthrough;
                Osd(HdrPassthrough ? "HDR passthrough" : $"HDR → SDR tonemapping ({ToneMapping})");
                break;
            case PlayerAction.ToggleDownmix:
                DownmixToStereo = !DownmixToStereo;
                Osd(DownmixToStereo ? "Downmix to stereo: on" : "Downmix to stereo: off (original channels)");
                break;
            case PlayerAction.CycleAudioTrack: p?.CycleTrack(TrackType.Audio); break;
            case PlayerAction.CycleSubtitleTrack: p?.CycleTrack(TrackType.Subtitle); break;
            case PlayerAction.ToggleSubtitles: p?.ToggleSubtitleVisibility(); break;
            case PlayerAction.SubtitleDelayUp: p?.AdjustSubtitleDelay(0.1); break;
            case PlayerAction.SubtitleDelayDown: p?.AdjustSubtitleDelay(-0.1); break;
            case PlayerAction.OpenFile: _ = OpenFile(); break;
            case PlayerAction.OpenFolder: _ = OpenFolder(); break;
            case PlayerAction.OpenLink: _ = OpenLink(); break;
            case PlayerAction.PasteAndPlay: _ = PasteAndPlay(); break;
            case PlayerAction.Stop: CloseTorrent(); p?.Stop(); break;
            case PlayerAction.CopyDiagnostics: _ = CopyDiagnostics(); break;
        }
    }

    private void CycleDeinterlace()
    {
        Deinterlace = Deinterlace switch
        {
            DeinterlaceMode.Auto => DeinterlaceMode.On,
            DeinterlaceMode.On => DeinterlaceMode.Off,
            _ => DeinterlaceMode.Auto,
        };
        Osd($"Deinterlace: {Deinterlace}");
    }

    private void Osd(string text) => _player?.ShowText(text);

    // ---- Auto-hiding controls -------------------------------------------------------------

    public static readonly TimeSpan ControlsHideDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>Two clicks on the video within this time toggle fullscreen.</summary>
    public static readonly TimeSpan DoubleClickTime = TimeSpan.FromMilliseconds(500);
    private DateTime _lastClick = DateTime.MinValue;

    /// <summary>A left click on the video; the second of two quick clicks toggles fullscreen.</summary>
    public void HandleClick(DateTime utcNow)
    {
        NotifyActivity();
        if (utcNow - _lastClick <= DoubleClickTime)
        {
            _lastClick = DateTime.MinValue; // a third click starts a new pair
            Execute(PlayerAction.ToggleFullscreen);
        }
        else _lastClick = utcNow;
    }

    partial void OnControlsVisibleChanged(bool value) => _player?.SetCursorHidden(!value);

    public void NotifyActivity()
    {
        _lastActivity = DateTime.UtcNow;
        ControlsVisible = true;
    }

    /// <summary>Called periodically by the view; hides controls after inactivity during playback.</summary>
    public void TickAutoHide(DateTime utcNow)
    {
        UpdateBufferingText(utcNow);
        var keepVisible = IsIdle || IsPaused || IsPointerOverControls || IsResolving || IsBuffering;
        if (keepVisible) { ControlsVisible = true; return; }
        if (utcNow - _lastActivity > ControlsHideDelay) ControlsVisible = false;
    }

    /// <summary>
    /// The pointer's screen position, polled by the view. mpv's video window swallows mouse
    /// events on some systems, so movement is detected here as well as from mpv.
    /// </summary>
    public void ReportPointer(int x, int y, bool insideWindow)
    {
        var moved = _lastPointer is { } last && last != (x, y);
        _lastPointer = (x, y);
        if (moved && insideWindow) NotifyActivity();
    }

    public static readonly TimeSpan QuipInterval = TimeSpan.FromSeconds(3);

    private void UpdateBufferingText(DateTime utcNow)
    {
        if (!IsBuffering && !IsResolving)
        {
            BufferingText = "";
            _lastQuip = DateTime.MinValue;
            return;
        }
        if (utcNow - _lastQuip < QuipInterval) return;
        _lastQuip = utcNow;
        BufferingText = BufferingQuips.Next(BufferingText);
        // On top of the video too, where people are looking while it stalls.
        if (IsBuffering && !IsIdle) _player?.ShowText(BufferingText, (int)QuipInterval.TotalMilliseconds + 300);
    }

    // ---- Status line ----------------------------------------------------------------------

    private CancellationTokenSource? _statusCts;

    public void ShowStatus(string message, bool sticky = false)
    {
        StatusMessage = message;
        _statusCts?.Cancel();
        if (sticky) return;
        var cts = _statusCts = new CancellationTokenSource();
        _ = Task.Delay(4000, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ui.Post(() => { if (StatusMessage == message) StatusMessage = ""; });
        }, TaskScheduler.Default);
    }

    // ---- Shutdown -------------------------------------------------------------------------

    public void SaveSettings()
    {
        try { _store.Save(Settings); }
        catch (IOException e) { ShowStatus($"Couldn't save settings: {e.Message}"); }
        catch (UnauthorizedAccessException) { ShowStatus("The app folder is read-only; settings won't be saved."); }
    }

    public void Dispose()
    {
        SaveSettings();
        // Stop yt-dlp rather than leave it running without us; cancelling kills it.
        _downloadCts?.Cancel();
        try { _downloadTask?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
        _player?.Dispose();
        _player = null;
        _torrents.Dispose();
        if (Settings.ClearTorrentCacheOnExit) ClearTorrentCache();
    }

    internal void ClearTorrentCache()
    {
        if (!Directory.Exists(_paths.TorrentCache)) return;
        foreach (var entry in new DirectoryInfo(_paths.TorrentCache).EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo d) d.Delete(recursive: true);
                else entry.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
