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

    // ---- Player lifecycle -----------------------------------------------------------------

    /// <summary>Called once the video surface exists (its native handle is mpv's render target).</summary>
    public void AttachPlayer(long? windowId)
    {
        if (_player is not null) return;
        _player = _playerFactory(windowId);
        _player.StateChanged += name => _ui.Post(() => SyncState(name));
        _player.TracksChanged += () => _ui.Post(SyncTracks);
        _player.KeyPressed += key => _ui.Post(() => HandleKey(key));
        _player.MouseMoved += () => _ui.Post(NotifyActivity);
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
            case "idle-active": IsIdle = s.IsIdle; break;
            case "media-title": MediaTitle = s.Title ?? ""; break;
            case "demuxer-cache-time": UpdateBuffered(s); break;
            case "hwdec-current": DecoderBadge = s.IsHardwareDecoding ? $"HW · {s.HwdecCurrent}" : "SW"; break;
            case "video-params/gamma" or "video-params/primaries": UpdateHdrBadge(); break;
            case "video-params/h" or "video-params/w": ResolutionBadge = ResolutionLabel(s.VideoWidth, s.VideoHeight); break;
            case "audio-params/channel-count" or "audio-out-params/channel-count": UpdateAudioBadge(); break;
        }
    }

    private void UpdateBuffered(PlayerState s) =>
        BufferedUntil = s.CacheTime is { } c ? Math.Min(Duration, Math.Max(c, s.Position)) : s.Position;

    internal void UpdateHdrBadge()
    {
        var s = _player?.State;
        if (s is null || !s.IsHdr) { HdrBadge = ""; return; }
        var kind = s.Transfer == "hlg" ? "HLG" : "HDR";
        HdrBadge = Settings.HdrMode == HdrMode.Passthrough ? $"{kind} passthrough" : $"{kind} → SDR";
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
        _player.Open(value, streaming: InputClassifier.IsStreaming(kind));
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

    // ---- Commands -------------------------------------------------------------------------

    [RelayCommand] private void TogglePause() => _player?.TogglePause();
    [RelayCommand] private void FrameStep() => _player?.FrameStep();
    [RelayCommand] private void FrameBackStep() => _player?.FrameBackStep();
    [RelayCommand] private void ToggleMute() => _player?.ToggleMute();
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
            case PlayerAction.OpenLink: _ = OpenLink(); break;
            case PlayerAction.PasteAndPlay: _ = PasteAndPlay(); break;
            case PlayerAction.Stop: CloseTorrent(); p?.Stop(); break;
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
