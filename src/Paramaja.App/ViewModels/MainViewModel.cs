using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly PlaybackRecorder _recorder = new();
    private MediaSource _source = MediaSource.None;
    [ObservableProperty] private string _renderProfileText = "";

    public PlayerSettings Settings { get; }
    public IDialogService? Dialogs { get; set; }
    public IPlayer? Player => _player;

    /// <summary>Raised when the view should toggle (true/false) or leave (false) fullscreen.</summary>
    public event Action<bool?>? FullscreenRequested;

    /// <param name="discPlayerFactory">
    /// The disc engine (libVLC), made on first use for the window it renders into; null where
    /// there's none, and discs then play through mpv without their menus.
    /// </param>
    public MainViewModel(PlayerSettings settings, SettingsStore store, PortablePaths paths,
        Func<long?, IPlayer> playerFactory, ITorrentService torrents, IUiDispatcher ui, YtDlp? ytdlp = null,
        Func<long, IPlayer>? discPlayerFactory = null)
    {
        _ytdlp = ytdlp ?? new YtDlp(AppContext.BaseDirectory);
        _discPlayerFactory = discPlayerFactory;
        Settings = settings;
        _store = store;
        _paths = paths;
        _playerFactory = playerFactory;
        _torrents = torrents;
        _ui = ui;
    }

    // ---- Observable state -----------------------------------------------------------------

    [ObservableProperty] private bool _isIdle = true;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isFullscreen;
    [ObservableProperty] private bool _controlsVisible = true;
    /// <summary>The mouse pointer is hidden over the player (see <see cref="CursorHidden"/>).</summary>
    [ObservableProperty] private bool _isCursorHidden;
    [ObservableProperty] private bool _isPointerOverControls;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _bufferedUntil;
    [ObservableProperty] private double _volume = 100;
    [ObservableProperty] private string _mediaTitle = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _hdrBadge = "";
    [ObservableProperty] private string _codecBadge = "";
    /// <summary>The decoder's own description of the codec, e.g. "hevc (HEVC (High Efficiency Video Coding))".</summary>
    [ObservableProperty] private string _codecTip = "";
    [ObservableProperty] private string _decoderBadge = "";
    [ObservableProperty] private string _resolutionBadge = "";
    [ObservableProperty] private string _audioBadge = "";
    /// <summary>The audio's channels and rate, and what the device gets where that differs.</summary>
    [ObservableProperty] private string _audioTip = "";
    [ObservableProperty] private bool _isTorrent;
    [ObservableProperty] private bool _isResolving;
    [ObservableProperty] private string _bufferingText = "";
    [ObservableProperty] private string _deinterlaceBadge = "";
    [ObservableProperty] private string _deinterlaceTip = "";
    [ObservableProperty] private string _ytDlpStatus = "";
    [ObservableProperty] private string _torrentStatus = "";
    [ObservableProperty] private TorrentFileItem? _selectedTorrentFile;
    /// <summary>Set by the view: the window's display is in HDR mode (only detectable on Windows).</summary>
    [ObservableProperty] private bool _displayIsHdr;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanDownload))] private bool _isDownloading;
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private string? _lastDownloadedFile;

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
    /// <summary>Chapters to step through: more than one, in what's playing (none while stopped).</summary>
    public bool HasChapters => !IsIdle && Chapters.Count > 1;
    public bool HasDiscTitles => !IsIdle && DiscTitles.Count > 1;
    public bool HasChapterMenu => HasChapters || HasDiscTitles;
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
        _player = _mpvPlayer = _playerFactory(windowId);
        Wire(_player);
        if (_osdInset > 0) _player.SetOsdTopInset(_osdInset);
        RenderProfileText = DescribeProfile(_player.RenderProfile);
        if (DisplayIsHdr) _player.SetDisplayHdr(true);
        OpenPendingWhenReady();
    }

    /// <summary>
    /// Something opened at startup (command line, file association) waits until every video
    /// surface exists: a disc opened before the disc engine's surface would fall back to mpv
    /// and lose its menus.
    /// </summary>
    private void OpenPendingWhenReady()
    {
        if (_player is null || (_discPlayerFactory is not null && _discSurface is null)) return;
        if (_pendingOpen is not { } p) return;
        _pendingOpen = null;
        PendingOpenTask = OpenInputAsync(p);
    }

    /// <summary>What was opened at startup, once the surfaces existed; tests await it.</summary>
    internal Task PendingOpenTask { get; private set; } = Task.CompletedTask;

    /// <summary>Events count only while their engine is the one playing.</summary>
    private void Wire(IPlayer player)
    {
        void Post(Action action) => _ui.Post(() => { if (ReferenceEquals(player, _player)) action(); });
        player.StateChanged += name => Post(() => SyncState(name));
        player.TracksChanged += () => Post(SyncTracks);
        player.ChaptersChanged += () => Post(SyncChapters);
        player.KeyPressed += key => Post(() => HandleKey(key));
        player.MouseMoved += () => Post(NotifyPointerMoved);
        player.MouseClicked += () => Post(() => { if (!ClicksFromSystem) HandleClick(DateTime.UtcNow); });
        player.FileLoaded += () => Post(() =>
        {
            _health.Reset();
            _recorder.Reset();
        });
        player.RenderProfileChanged += p => Post(() => RenderProfileText = DescribeProfile(p));
        player.EndFile += (reason, error) => Post(() =>
        {
            if (reason == EndFileReason.Error) ShowStatus($"Can't play this: {error}");
        });
    }

    // ---- Engines: mpv for everything, libVLC for discs (their menus) ----------------------------

    private IPlayer? _mpvPlayer;
    private IPlayer? _discPlayer;
    private readonly Func<long, IPlayer>? _discPlayerFactory;
    private long? _discSurface;

    /// <summary>A disc is playing through libVLC: the view shows its video surface instead of mpv's.</summary>
    [ObservableProperty] private bool _isDiscMode;

    /// <summary>The native window the disc engine renders into, once the view has made it.</summary>
    public void AttachDiscSurface(long windowId)
    {
        _discSurface = windowId;
        OpenPendingWhenReady();
    }

    private Task<IPlayer?>? _discEngineStart;

    /// <summary>
    /// The disc engine, made on first use; null if there isn't one (or it failed to load). It's
    /// made off the UI thread: libVLC loading its plugins could take seconds (on Windows, before
    /// it had its plugin cache), and the window froze meanwhile.
    /// </summary>
    private Task<IPlayer?> DiscEngineAsync()
    {
        if (_discPlayer is not null || _discPlayerFactory is null || _discSurface is not { } surface) return Task.FromResult(_discPlayer);
        return _discEngineStart ??= StartDiscEngineAsync(_discPlayerFactory, surface);
    }

    private async Task<IPlayer?> StartDiscEngineAsync(Func<long, IPlayer> factory, long surface)
    {
        ShowStatus("Starting the disc player…", sticky: true);
        try
        {
            var engine = await Task.Run(() => factory(surface));
            Wire(engine);
            if (_osdInset > 0) engine.SetOsdTopInset(_osdInset);
            _discPlayer = engine;
            ShowStatus("");
        }
        catch (Exception e) when (e is DllNotFoundException or InvalidOperationException or TypeInitializationException
                                      or LibVLCSharp.Shared.VLCException or AggregateException)
        {
            ShowStatus("Disc menus aren't available in this build: playing the disc without them.");
        }
        return _discPlayer;
    }

    /// <summary>A DVD or Blu-ray plays through libVLC, with its menus: the Disc menu button applies.</summary>
    [ObservableProperty] private bool _canShowDiscMenu;

    /// <summary>Hands playback to another engine, stopping the one that was playing.</summary>
    private void UseEngine(IPlayer next)
    {
        if (ReferenceEquals(next, _player)) return;
        _player?.Stop();
        _player = next;
        IsDiscMode = ReferenceEquals(next, _discPlayer);
        if (!IsDiscMode) CanShowDiscMenu = false;
        next.SetCursorHidden(IsCursorHidden);
        SyncTracks();
        SyncChapters();
        SyncAllState();
    }

    /// <summary>
    /// Everything shown comes from the engine now playing. Each engine only reports changes,
    /// so without this a disc's "paused" or deinterlacing badge stayed up for the next file.
    /// (Idle isn't taken over: the engine reports it itself as it opens, and taking it a moment
    /// early would flash the welcome screen.)
    /// </summary>
    private void SyncAllState()
    {
        foreach (var name in EngineStateNames) SyncState(name);
        ShowPosition(force: true);
    }

    private static readonly string[] EngineStateNames =
    [
        "duration", "pause", "mute", "volume", "paused-for-cache", "media-title", "deinterlace-active",
        "hwdec-current", "video-params/gamma", "hardware-decoding", "chapter", "video-params/w",
        "audio-params/channel-count", "audio-params/samplerate", "video-format", "video-codec", "stereo-channel", "has-video",
        "video-params/aspect", "picture-fit", "stream-heights",
    ];

    /// <summary>Back to the disc's root menu (the Menu button).</summary>
    [RelayCommand] private void ShowDiscMenu() => _player?.ShowDiscMenu();

    private void SyncState(string name)
    {
        if (_player is null) return;
        var s = _player.State;
        switch (name)
        {
            case "time-pos" or "demuxer-cache-time": ShowPosition(); break;
            case "duration": Duration = s.Duration; break;
            case "pause":
                IsPaused = s.IsPaused;
                ShowPosition(force: true);
                if (IsPaused) NotifyActivity();
                break;
            case "mute": IsMuted = s.IsMuted; break;
            case "volume": Volume = s.Volume; break;
            case "deinterlace-active": UpdateDeinterlace(s); break;
            case "paused-for-cache": IsBuffering = s.IsBuffering; break;
            case "idle-active":
                IsIdle = s.IsIdle;
                OnPropertyChanged(nameof(CanDownload));
                OnAudioOnlyChanged();
                UpdateCodecBadge();
                break;
            case "has-video":
                OnAudioOnlyChanged();
                UpdateCodecBadge();
                break;
            case "stream-heights": FillStreamQualities(s.StreamHeights); break;
            case "video-params/aspect" or "picture-fit": OnPictureChanged(); break;
            case "eof-reached":
                // A torrent's files play one after the other (mpv stays at the end of each).
                if (s.EofReached && IsTorrent) StepTorrentFile(1);
                break;
            case "media-title": MediaTitle = s.Title ?? ""; break;
            case "video-format" or "video-codec": UpdateCodecBadge(); break;
            case "stereo-channel": OnPropertyChanged(nameof(StereoChannelIndex)); break;
            case "hwdec-current": DecoderBadge = s.IsHardwareDecoding ? $"HW · {s.HwdecCurrent}" : "SW"; break;
            case "video-params/gamma" or "video-params/primaries": UpdateHdrBadge(); break;
            case "hardware-decoding":
                OnPropertyChanged(nameof(HasVideoSwitches));
                OnPropertyChanged(nameof(HardwareDecodingApplies));
                OnPropertyChanged(nameof(HardwareDecoding));
                break;
            case "chapter": MarkCurrentChapter(); break;
            case "video-params/h" or "video-params/w": ResolutionBadge = ResolutionLabel(s.VideoWidth, s.VideoHeight); break;
            case "audio-params/channel-count" or "audio-out-params/channel-count" or "audio-params/samplerate"
                or "audio-out-params/samplerate":
                UpdateAudioBadge();
                OnPropertyChanged(nameof(IsSurroundAudio));
                OnPropertyChanged(nameof(IsStereoAudio));
                break;
        }
    }

    private void UpdateBuffered(PlayerState s) =>
        BufferedUntil = s.CacheTime is { } c ? Math.Min(Duration, Math.Max(c, s.Position)) : s.Position;

    /// <summary>How often the position shown (time and seek bar) may change while the controls are up.</summary>
    public static readonly TimeSpan PositionInterval = TimeSpan.FromMilliseconds(200);
    private DateTime _positionShownAt = DateTime.MinValue;

    /// <summary>
    /// mpv reports the position with every frame. Each change re-lays out and re-renders the
    /// overlay bars (on Windows they're never hidden, only parked off-screen), so it's shown at
    /// most 5 times a second while the controls are up and not at all while they're hidden; a
    /// jump (a seek) shows at once.
    /// </summary>
    private void ShowPosition(bool force = false)
    {
        if (_player is null) return;
        var s = _player.State;
        var now = DateTime.UtcNow;
        var jumped = Math.Abs(s.Position - Position) >= 1.5;
        if (!force && !jumped && (!ControlsVisible || now - _positionShownAt < PositionInterval)) return;
        _positionShownAt = now;
        Position = s.Position;
        UpdateBuffered(s);
    }

    /// <summary>The playing video is interlaced: only then is there a deinterlacing switch.</summary>
    public bool IsInterlacedVideo => _player?.State.SourceInterlaced == true;

    private void UpdateDeinterlace(PlayerState s)
    {
        OnPropertyChanged(nameof(HasVideoSwitches));
        DeinterlaceBadge = !s.SourceInterlaced ? "" : s.DeinterlaceActive ? s.DeinterlaceFilter ?? "BWDIF" : "Interlaced";
        DeinterlaceTip = !s.SourceInterlaced ? ""
            : (s.InterlaceDetectedBy == PlayerState.ByPicture ? "Interlaced: seen in the picture (the video isn't flagged)" : "Interlaced: flagged by the video")
              + (s.DeinterlaceActive ? $". {s.DeinterlaceFilter} makes a frame of each field." : ". Deinterlacing is off for this video.");
        OnPropertyChanged(nameof(IsInterlacedVideo));
        OnPropertyChanged(nameof(Deinterlacing));
    }

    /// <summary>The playing video is HDR: only then does the HDR / tonemapping menu apply.</summary>
    public bool IsHdrVideo => _player?.State.IsHdr == true;

    /// <summary>5.1, 7.1 and the like: only then is there anything to downmix to stereo.</summary>
    public bool IsSurroundAudio => _player?.State.SourceChannels > 2;

    /// <summary>A stereo source: only then can one of its channels play on both speakers.</summary>
    public bool IsStereoAudio => _player?.State.SourceChannels == 2;

    /// <summary>
    /// The playing video gets hardware decoding: it helps (HEVC, AV1, or 1080p up) and the GPU
    /// can decode it. Only then is there a switch; it's decided anew for each video.
    /// </summary>
    public bool HardwareDecodingApplies => _player?.State.HardwareDecodingApplies == true;

    /// <summary>Settings → Developer has a switch for the playing video (deinterlacing or hardware decoding).</summary>
    public bool HasVideoSwitches => IsInterlacedVideo || HardwareDecodingApplies;

    /// <summary>Audio alone (a music file, an Audio CD, an SACD): no frames to step through, no picture to shape.</summary>
    public bool IsAudioOnly => !IsIdle && _player?.State.HasVideo == false;

    private void OnAudioOnlyChanged()
    {
        OnPropertyChanged(nameof(IsAudioOnly));
        OnPictureChanged();
    }

    internal void UpdateHdrBadge()
    {
        OnPropertyChanged(nameof(IsHdrVideo));
        var s = _player?.State;
        if (s is null || !s.IsHdr) { HdrBadge = ""; return; }
        var kind = s.Transfer == "hlg" ? "HLG" : "HDR";
        HdrBadge = HdrPassthrough ? $"{kind} passthrough" : $"{kind} → SDR";
    }

    /// <summary>
    /// The source's channels, and "→ stereo" only where the downmix switch folds them: the badge
    /// says what the player does. A device set up with fewer speakers than the source (Windows
    /// set to stereo) gets a fold-down from the system all the same; the tooltip tells that.
    /// </summary>
    internal void UpdateAudioBadge()
    {
        var s = _player?.State;
        if (s?.SourceChannels is not { } src) { AudioBadge = ""; AudioTip = ""; return; }
        var label = TrackInfo.ChannelLabel(src);
        var downmixed = DownmixApplies(src);
        AudioBadge = downmixed ? $"{label} → stereo" : label;
        AudioTip = AudioDescription(s, src, downmixed);
    }

    private bool DownmixApplies(int sourceChannels) => Settings.DownmixToStereo && !Settings.AudioPassthrough && sourceChannels > 2;

    internal static string AudioDescription(PlayerState s, int src, bool downmixed)
    {
        var label = TrackInfo.ChannelLabel(src);
        var lines = new List<string> { s.SourceSampleRate is { } rate ? $"{label} · {RateLabel(rate)}" : label };
        if (downmixed) lines.Add("Downmixed to stereo (Settings → Audio)");
        else if (s.OutputChannels is { } o && o < src)
            lines.Add($"Your audio device is set to {TrackInfo.ChannelLabel(o)}, so the system mixes this {label} down for it");
        if (s.OutputSampleRate is { } outRate && s.SourceSampleRate is { } inRate && outRate != inRate)
            lines.Add($"Played at {RateLabel(outRate)}");
        return string.Join("\n", lines);
    }

    internal static string RateLabel(int hz) =>
        (hz / 1000.0).ToString(hz % 1000 == 0 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + " kHz";

    /// <summary>
    /// The codec badge: what people know the source by where that's a disc (an Audio CD, an SACD,
    /// a DVD more than its MPEG-2), otherwise the video's codec.
    /// </summary>
    private void UpdateCodecBadge()
    {
        var s = _player?.State;
        var codec = CodecLabels.Describe(s?.VideoFormat);
        var playing = s is { IsIdle: false };
        (CodecBadge, CodecTip) = _openedKind switch
        {
            InputKind.AudioCd when playing => ("CD", "Audio CD · 44.1 kHz / 16-bit"),
            InputKind.Sacd when playing => ("SACD", SacdTip()),
            InputKind.Dvd when codec.Length > 0 => ("DVD", !string.IsNullOrEmpty(s?.VideoCodec) ? s.VideoCodec! : $"DVD ({codec})"),
            _ => (codec, !string.IsNullOrEmpty(s?.VideoCodec) ? s.VideoCodec! : codec),
        };
    }

    private string SacdTip()
    {
        var area = _player?.Titles.FirstOrDefault(t => t.Selected)?.Title;
        var name = area is null ? "" : " · " + area.Split(" (")[0].ToLowerInvariant() + " area";
        return $"Super Audio CD · DSD64 (2.8 MHz){name}";
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
        OnChaptersShownChanged();
        if (_openedKind == InputKind.Sacd) UpdateCodecBadge(); // the area is in its tooltip
    }

    partial void OnIsIdleChanged(bool value) => OnChaptersShownChanged();

    private void OnChaptersShownChanged()
    {
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
        if (_player is null || (_discPlayerFactory is not null && _discSurface is null))
        {
            _pendingOpen = value;
            return;
        }
        _openedKind = kind;
        UpdateCodecBadge();
        ForgetStreamQualities();
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
        if (kind == InputKind.Sacd)
        {
            // libVLC doesn't read SACDs; mpv does, through Paramaja's own reader.
            UseEngine(_mpvPlayer!);
            try { _player!.OpenSacd(value); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or MpvException)
            {
                ShowStatus($"Can't play this SACD image: {e.Message}");
            }
        }
        else if (kind is InputKind.Dvd or InputKind.Bluray or InputKind.AudioCd)
        {
            // libVLC shows the disc's menus and plays CDs; without it, mpv plays a disc's main title.
            UseEngine(await DiscEngineAsync() ?? _mpvPlayer!);
            CanShowDiscMenu = IsDiscMode && kind is not InputKind.AudioCd;
            try
            {
                if (kind == InputKind.AudioCd) _player!.OpenAudioCd(value);
                else _player!.OpenDisc(value, bluray: kind == InputKind.Bluray);
            }
            catch (MpvException)
            {
                ShowStatus(kind switch
                {
                    InputKind.Dvd => "This build can't play DVD folders.",
                    InputKind.Bluray => "This build can't play Blu-ray folders.",
                    _ => "This build can't play Audio CDs.",
                });
                return;
            }
        }
        else if (kind == InputKind.File && Path.GetExtension(value).Equals(".cue", StringComparison.OrdinalIgnoreCase))
        {
            // A CD rip: its cue sheet's tracks play as one timeline of the files really there,
            // with the tracks as chapters (CueSheet), under the album's title.
            UseEngine(_mpvPlayer!);
            string? album = null;
            try { album = CueSheet.Read(value).Title; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            _player!.Open(CueSheet.PlayableUrl(value) ?? value,
                string.IsNullOrWhiteSpace(album) ? Path.GetFileNameWithoutExtension(value) : album);
        }
        else
        {
            UseEngine(_mpvPlayer!);
            _player!.Open(value, streaming: InputClassifier.IsStreaming(kind));
        }
    }

    /// <summary>What was opened last (a DVD shows as "DVD" rather than by its codec, an Audio CD as "CD", an SACD as "SACD").</summary>
    private InputKind _openedKind;

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

            // Its videos and music, in the order of their names: a playlist to go through.
            Replace(TorrentFiles, info.Files
                .Where(IsMediaFile)
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Select(f => new TorrentFileItem(f.Index, f.FileName, TimeFormat.Bytes(f.Length))));
            OnPropertyChanged(nameof(HasTorrentFiles));

            // The engine starts at the main video (the largest); an album starts at its first track.
            var (streamUrl, fileIdx) = (info.StreamUrl, info.SelectedFile);
            if (!info.Files.Any(f => f.IsVideo) && TorrentFiles.FirstOrDefault() is { } first && first.Index != fileIdx)
                (streamUrl, fileIdx) = (_torrents.SelectFile(info.Id, first.Index), first.Index);
            _selectingTorrentFile = true;
            SelectedTorrentFile = TorrentFiles.FirstOrDefault(f => f.Index == fileIdx);
            _selectingTorrentFile = false;

            if (streamUrl is null || fileIdx is not { } index)
            {
                ShowStatus("This torrent has no playable files.");
                return;
            }
            var file = info.Files.First(f => f.Index == index);
            SetSource(MediaSource.Torrent);
            UseEngine(_mpvPlayer!);
            _player!.Open(streamUrl, file.FileName, streaming: true);
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
            UseEngine(_mpvPlayer!);
            _player!.Open(info.StreamUrl, info.Name, streaming: true);
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

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".wv", ".ape", ".dsf", ".dff", ".mka", ".ac3", ".dts",
    };

    /// <summary>A video, or music: what a torrent's playlist is made of.</summary>
    internal static bool IsMediaFile(TorrentFile file) => file.IsVideo || AudioExtensions.Contains(Path.GetExtension(file.Path));

    /// <summary>The previous or next file of a torrent with several; false at either end.</summary>
    private bool StepTorrentFile(int delta)
    {
        if (!HasTorrentFiles || SelectedTorrentFile is not { } current) return false;
        var i = TorrentFiles.IndexOf(current) + delta;
        if (i < 0 || i >= TorrentFiles.Count) return false;
        SelectedTorrentFile = TorrentFiles[i];
        return true;
    }

    [RelayCommand] private void PreviousFile() => StepTorrentFile(-1);
    [RelayCommand] private void NextFile() => StepTorrentFile(1);

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

    /// <summary>The downloader, prefilled with the playing web video's link.</summary>
    [RelayCommand]
    private Task Download() => Dialogs?.ShowDownloaderAsync(_webUrl) ?? Task.CompletedTask;

    /// <summary>The downloader, with an empty box (or a link from the clipboard).</summary>
    [RelayCommand]
    private async Task OpenDownloader()
    {
        if (Dialogs is null) return;
        if (!HasYtDlp) { ShowStatus("Downloading needs yt-dlp, which isn't bundled with this build."); return; }
        await Dialogs.ShowDownloaderAsync(null);
    }

    internal YtDlp YtDlp => _ytdlp;
    public string DownloadsFolderName => Path.GetFileName(_paths.Downloads.TrimEnd(Path.DirectorySeparatorChar));

    /// <summary>
    /// Saves a page's video to the Downloads folder: the best video up to
    /// <paramref name="maxHeight"/> (0 for the best of all) merged with the best audio, or with
    /// <paramref name="audioOnly"/> just the best audio.
    /// </summary>
    public async Task StartDownloadAsync(string url, int maxHeight = 0, bool audioOnly = false)
    {
        if (IsDownloading) return;
        using var cts = _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadStatus = "Starting download…";
        try
        {
            var task = Task.Run(() => _ytdlp.DownloadAsync(url, _paths.Downloads,
                p => _ui.Post(() => { if (IsDownloading) DownloadStatus = p.ToString(); }), cts.Token, maxHeight, audioOnly));
            _downloadTask = task;
            var file = await task;
            LastDownloadedFile = file;
            ShowStatus($"Saved to {DownloadsFolderName}: {Path.GetFileName(file)}");
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
    [RelayCommand] private void Stop() => Execute(PlayerAction.Stop);
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

    /// <summary>
    /// A DVD or Blu-ray (with its menus) or an Audio CD, from a drive, a folder or a disc image (an
    /// SACD's too); a CD rip's folder plays its cue sheet, with the tracks as chapters.
    /// </summary>
    [RelayCommand]
    private async Task OpenDisc()
    {
        if (Dialogs is null) return;
        if (await Dialogs.PickDiscAsync() is not { } root) return;
        var (kind, value) = InputClassifier.Classify(root);
        var cueSheet = kind == InputKind.File && Path.GetExtension(value).Equals(".cue", StringComparison.OrdinalIgnoreCase);
        if (kind is InputKind.Dvd or InputKind.Bluray or InputKind.AudioCd or InputKind.Sacd || cueSheet) await OpenInputAsync(root);
        else ShowStatus("That isn't a disc: look for a folder with VIDEO_TS or BDMV inside, a CD rip's .cue sheet, or a DVD, Blu-ray or SACD .iso.");
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
        var (cpu, memory) = ProcessUsage();
        var sample = _player.SampleHealth() with { CpuPercent = cpu, MemoryMb = memory };
        _health.Add(sample);
        _recorder.Add(sample);
        if (Settings.RenderQuality == RenderQuality.Auto && _player.RenderProfile != RenderProfile.Fast && _health.RenderingStruggling)
        {
            _player.FallBackToFastRendering();
            _health.Reset(); // judge the new profile on its own frames
            const string msg = "Frames were dropping, so Paramaja switched to Fast rendering (Settings → Developer → Rendering quality)";
            _player.ShowText(msg, 5000);
            ShowStatus(msg);
        }
    }

    private (TimeSpan Cpu, DateTime At)? _lastCpu;

    /// <summary>The app's CPU use since the last reading (100 = one core busy) and its memory.</summary>
    private (double? Cpu, double Memory) ProcessUsage()
    {
        using var process = Process.GetCurrentProcess();
        var (cpu, now) = (process.TotalProcessorTime, DateTime.UtcNow);
        double? percent = _lastCpu is { } last && now > last.At ? (cpu - last.Cpu).TotalSeconds / (now - last.At).TotalSeconds * 100 : null;
        _lastCpu = (cpu, now);
        return (percent is { } p ? Math.Round(p, 1) : null, Math.Round(process.WorkingSet64 / 1048576.0));
    }

    // ---- Create Log --------------------------------------------------------------------------

    [ObservableProperty] private string _logStatus = "";
    [ObservableProperty] private string? _lastLogFile;

    /// <summary>
    /// Saves what's been recorded since the playing video started (every second's playback
    /// statistics, the machine, the video and how it's played, the engine's messages) to a file in
    /// the logs folder, for benchmarking and for whoever is helping.
    /// </summary>
    [RelayCommand]
    private void CreateLog()
    {
        try
        {
            var file = PlaybackLog.Write(_paths.Logs, BuildLog(), DateTime.Now);
            LastLogFile = file;
            LogStatus = $"Saved {Path.GetFileName(file)} in the logs folder";
            ShowStatus($"Log saved: {Path.GetFileName(file)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogStatus = $"Couldn't save the log: {e.Message}";
        }
    }

    internal string BuildLog() => PlaybackLog.Build(new PlaybackLogInput(
        _player is null || IsIdle ? null : _player.State, Settings, _player?.RenderProfile, _health, _recorder.Samples,
        _source, TorrentStatus, IsDiscMode ? "libVLC (disc)" : "mpv",
        _player is null || IsIdle ? [] : _player.Details(), _player?.RecentMessages() ?? [], DateTime.Now));

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try { Directory.CreateDirectory(_paths.Logs); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Dialogs?.OpenFolder(_paths.Logs);
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
        // On a disc menu, the arrows and Enter move through it instead of seeking.
        if (MenuKey(key) is { } nav && _player?.IsInDiscMenu == true)
        {
            NotifyActivity();
            _player.NavigateMenu(nav);
            return true;
        }
        if (!ShortcutMap.TryGet(key, out var action)) return false;
        NotifyActivity();
        Execute(action);
        return true;
    }

    private static MenuNavigation? MenuKey(string key) => key switch
    {
        "UP" => MenuNavigation.Up,
        "DOWN" => MenuNavigation.Down,
        "LEFT" => MenuNavigation.Left,
        "RIGHT" => MenuNavigation.Right,
        "ENTER" => MenuNavigation.Activate,
        _ => null,
    };

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
            case PlayerAction.ToggleDeinterlace: ToggleDeinterlacing(); break;
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
            case PlayerAction.SubtitleDelayUp: ShiftSubtitles(0.1); break;
            case PlayerAction.SubtitleDelayDown: ShiftSubtitles(-0.1); break;
            case PlayerAction.OpenFile: _ = OpenFile(); break;
            case PlayerAction.OpenFolder: _ = OpenFolder(); break;
            case PlayerAction.OpenLink: _ = OpenLink(); break;
            case PlayerAction.PasteAndPlay: _ = PasteAndPlay(); break;
            case PlayerAction.Stop: CloseTorrent(); p?.Stop(); break;
            case PlayerAction.CopyDiagnostics: _ = CopyDiagnostics(); break;
        }
    }

    /// <summary>Shows the total shift after every press, e.g. "Subtitles +0.1s", then "+0.2s".</summary>
    [RelayCommand]
    private void ShiftSubtitles(double seconds)
    {
        if (_player is null) return;
        Osd($"Subtitles {FormatShift(_player.AdjustSubtitleDelay(seconds))}");
    }

    internal static string FormatShift(double seconds) =>
        seconds == 0 ? "0.0s" : seconds.ToString("+0.0#;-0.0#", System.Globalization.CultureInfo.InvariantCulture) + "s";

    /// <summary>D: the deinterlacing switch, for an interlaced video; others have nothing to deinterlace.</summary>
    private void ToggleDeinterlacing()
    {
        if (IsInterlacedVideo) Deinterlacing = !Deinterlacing;
        else Osd("This video isn't interlaced: nothing to deinterlace");
    }

    private void Osd(string text) => _player?.ShowText(text);

    // ---- Auto-hiding controls -------------------------------------------------------------

    public static readonly TimeSpan ControlsHideDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>In fullscreen the pointer hides this long after it last moved, paused or not.</summary>
    public static readonly TimeSpan CursorHideDelay = TimeSpan.FromSeconds(3);
    private DateTime _lastPointerMove = DateTime.UtcNow;

    /// <summary>
    /// Set by the view: clicks on the video are read from the OS (VideoClicks), so the engine's own
    /// reports of them, where it makes any, would count each click twice.
    /// </summary>
    public bool ClicksFromSystem { get; set; }

    /// <summary>Two clicks on the video within this time toggle fullscreen.</summary>
    public static readonly TimeSpan DoubleClickTime = TimeSpan.FromMilliseconds(500);
    private DateTime _lastClick = DateTime.MinValue;

    /// <summary>A left click on the video; the second of two quick clicks toggles fullscreen.</summary>
    public void HandleClick(DateTime utcNow)
    {
        NotifyPointerMoved();
        // On a disc menu a click presses a button; quick clicks there aren't a double-click.
        if (_player?.IsInDiscMenu == true) return;
        if (utcNow - _lastClick <= DoubleClickTime)
        {
            _lastClick = DateTime.MinValue; // a third click starts a new pair
            Execute(PlayerAction.ToggleFullscreen);
        }
        else _lastClick = utcNow;
    }

    partial void OnControlsVisibleChanged(bool value)
    {
        UpdateCursor(DateTime.UtcNow);
        if (value) ShowPosition(force: true); // it isn't kept current while they're hidden
    }

    partial void OnIsFullscreenChanged(bool value) => UpdateCursor(DateTime.UtcNow);

    partial void OnIsCursorHiddenChanged(bool value) => _player?.SetCursorHidden(value);

    /// <summary>
    /// Whether the pointer is hidden. In fullscreen: 3 s after it last moved, whether the video
    /// plays or is paused, but never while a menu is open or the pointer is on the bars (or on
    /// the welcome screen); only moving it brings it back. In a window it goes with the controls.
    /// </summary>
    internal bool CursorHidden(DateTime utcNow) => IsFullscreen
        ? !IsIdle && !IsMenuOpen && !IsPointerOverControls && utcNow - _lastPointerMove > CursorHideDelay
        : !ControlsVisible;

    private void UpdateCursor(DateTime utcNow) => IsCursorHidden = CursorHidden(utcNow);

    /// <summary>The mouse moved (or clicked): the pointer shows again, and the controls with it.</summary>
    public void NotifyPointerMoved()
    {
        _lastPointerMove = DateTime.UtcNow;
        NotifyActivity();
        UpdateCursor(_lastPointerMove);
    }

    // A menu of the bars is open: the bars stay up. They used to hide (Linux) or move off screen
    // (Windows) under a menu the pointer rested on, and the menu followed them to the corner.
    private int _openMenus;
    public bool IsMenuOpen => _openMenus > 0;

    public void MenuOpened()
    {
        _openMenus++;
        OnPropertyChanged(nameof(IsMenuOpen));
        NotifyActivity();
    }

    public void MenuClosed()
    {
        if (_openMenus == 0) return;
        _openMenus--;
        OnPropertyChanged(nameof(IsMenuOpen));
        NotifyActivity();
    }

    public void NotifyActivity()
    {
        _lastActivity = DateTime.UtcNow;
        ControlsVisible = true;
    }

    /// <summary>Called periodically by the view; hides controls after inactivity during playback.</summary>
    public void TickAutoHide(DateTime utcNow)
    {
        UpdateBufferingText(utcNow);
        ReconcilePause();
        if (ControlsVisible && _player is { } p && p.State.Position != Position) ShowPosition();
        var keepVisible = IsIdle || IsPaused || IsPointerOverControls || IsResolving || IsBuffering || IsMenuOpen;
        if (keepVisible) ControlsVisible = true;
        else if (utcNow - _lastActivity > ControlsHideDelay) ControlsVisible = false;
        UpdateCursor(utcNow);
    }

    /// <summary>
    /// The pointer's screen position, polled by the view. mpv's video window swallows mouse
    /// events on some systems, so movement is detected here as well as from mpv.
    /// </summary>
    public void ReportPointer(int x, int y, bool insideWindow)
    {
        var moved = _lastPointer is { } last && last != (x, y);
        _lastPointer = (x, y);
        if (moved && insideWindow) NotifyPointerMoved();
    }

    /// <summary>
    /// The play / pause button goes by the engine's events; a missed or stray one (an engine
    /// switch, a disc's still frame) left it showing "play" while the video played. The engine is
    /// asked directly a few times a second.
    /// </summary>
    private void ReconcilePause()
    {
        if (_player is null || IsIdle || _player.QueryPaused() is not { } paused || paused == IsPaused) return;
        IsPaused = paused;
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
        // On the video too, where people are looking while it stalls, unless the top bar (which
        // shows it) is up: then it would only say it twice.
        if (IsBuffering && !IsIdle && !BarsShown) _player?.ShowText(BufferingText, (int)QuipInterval.TotalMilliseconds + 300);
    }

    // ---- On-screen messages ----------------------------------------------------------------

    /// <summary>Set by the view: the control bars are on screen.</summary>
    public bool BarsShown { get; set; }

    /// <summary>The share of the video's height the top bar covers; the engines' messages start below it.</summary>
    private double _osdInset;

    public void SetOsdInset(double fraction)
    {
        if (Math.Abs(fraction - _osdInset) < 0.002) return;
        _osdInset = fraction;
        _mpvPlayer?.SetOsdTopInset(fraction);
        _discPlayer?.SetOsdTopInset(fraction);
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
        _mpvPlayer?.Dispose();
        _discPlayer?.Dispose();
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
