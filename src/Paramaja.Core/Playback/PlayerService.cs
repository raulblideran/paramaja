using System.Globalization;
using Paramaja.Core.Input;
using Paramaja.Core.Mpv;
using Paramaja.Core.Settings;

namespace Paramaja.Core.Playback;

/// <summary>
/// Owns the mpv instance and exposes playback as simple commands + state.
/// All events fire on mpv's event thread.
/// </summary>
public sealed class PlayerService : IPlayer
{
    public const string KeyMessage = "paramaja-key";
    public const string MouseMessage = "paramaja-mouse";
    public const string ClickMessage = "paramaja-click";

    private readonly MpvClient _mpv;
    private readonly PortablePaths _paths;

    public PlayerSettings Settings { get; }
    public PlayerState State { get; } = new();
    public IReadOnlyList<TrackInfo> Tracks { get; private set; } = [];
    public IReadOnlyList<ChapterInfo> Chapters { get; private set; } = [];
    public IReadOnlyList<DiscTitleInfo> Titles { get; private set; } = [];
    public MpvClient Mpv => _mpv;

    public event Action<string>? StateChanged;
    public event Action? TracksChanged;
    public event Action? ChaptersChanged;
    public event Action<string>? KeyPressed;
    public event Action? MouseMoved;
    public event Action? MouseClicked;
    public event Action? FileLoaded;
    public event Action<EndFileReason, string?>? EndFile;
    public event Action<MpvLogMessage>? Log;

    /// <param name="extraOptions">Applied after the defaults — tests use it for vo=null etc.</param>
    public PlayerService(PlayerSettings settings, PortablePaths paths, long? windowId = null,
        IEnumerable<KeyValuePair<string, string>>? extraOptions = null)
    {
        Settings = settings;
        _paths = paths;
        _paths.EnsureCreated();
        _mpv = new MpvClient();
        _hwdec = "no"; // decided per file, once its codec and size are known
        RenderProfile = MpvOptionsBuilder.InitialProfile(settings.RenderQuality);
        foreach (var (k, v) in MpvOptionsBuilder.Build(settings, paths, windowId))
        {
            try { _mpv.SetOption(k, v); }
            catch (MpvException e) when (e.Code == MpvException.OptionNotFound && MpvOptionsBuilder.OptionalOptions.Contains(k)) { }
        }
        foreach (var (k, v) in extraOptions ?? []) _mpv.SetOption(k, v);
        Wire();
        _mpv.Initialize();
        Observe();
        // Tracks are chosen but the decoders haven't started: deciding here avoids restarting
        // the decoder a moment into playback (which raced with interlace detection).
        _mpv.AddHook("on_preloaded", DecideHardwareDecoding);
        BindKeys();
    }

    private void Wire()
    {
        _mpv.PropertyChanged += OnProperty;
        _mpv.FileLoaded += () =>
        {
            RefreshTracks();
            RefreshChapters();
            FileLoaded?.Invoke();
        };
        _mpv.StartFile += () =>
        {
            // Every file is judged on its own frames: drop the previous file's verdict.
            _sourceInterlaced = false;
            State.SourceInterlaced = false;
            if (Settings.Deinterlace == DeinterlaceMode.Auto) ApplyDeinterlace();
        };
        _mpv.EndFile += (reason, err) =>
            EndFile?.Invoke(reason, reason == EndFileReason.Error ? MpvClient.ErrorString(err) : null);
        _mpv.ClientMessage += args =>
        {
            if (args.Length >= 2 && args[0] == KeyMessage) KeyPressed?.Invoke(args[1]);
            else if (args.Length >= 1 && args[0] == MouseMessage) MouseMoved?.Invoke();
            else if (args.Length >= 1 && args[0] == ClickMessage) MouseClicked?.Invoke();
        };
        _mpv.LogMessage += m => Log?.Invoke(m);
    }

    private void Observe()
    {
        foreach (var p in new[] { "time-pos", "duration", "volume", "cache-buffering-state", "demuxer-cache-time",
                     "container-fps", "estimated-vf-fps" })
            _mpv.ObserveDouble(p);
        foreach (var p in new[] { "pause", "mute", "paused-for-cache", "eof-reached", "idle-active", "seeking", "video-frame-info/interlaced" })
            _mpv.ObserveFlag(p);
        foreach (var p in new[] { "audio-params/channel-count", "audio-out-params/channel-count", "video-params/w",
                     "video-params/h", "track-list/count", "chapter", "chapter-list/count", "current-edition" })
            _mpv.ObserveLong(p);
        foreach (var p in new[] { "media-title", "path", "hwdec-current", "video-codec", "video-params/gamma",
                     "video-params/primaries", "deinterlace", "vf", "video-format", "current-vo" })
            _mpv.ObserveString(p);
    }

    /// <summary>
    /// When the video surface has focus mpv gets the keystrokes; forward each of our
    /// shortcuts back to us as a client message so handling is identical to the app window.
    /// Double-clicks are timed by the app from single clicks: mpv's own MBTN_LEFT_DBL isn't
    /// reliably generated for an embedded window. On Windows the app reads clicks from the OS
    /// instead (VideoClicks), as mpv's window doesn't reliably report them there.
    /// </summary>
    private void BindKeys()
    {
        foreach (var key in ShortcutMap.Default.Keys.Where(k => k != "MBTN_LEFT_DBL"))
            _mpv.TryCommand("keybind", key, $"script-message {KeyMessage} \"{key}\"");
        _mpv.TryCommand("keybind", "MOUSE_MOVE", $"script-message {MouseMessage}");
        if (!OperatingSystem.IsWindows()) _mpv.TryCommand("keybind", "MBTN_LEFT", $"script-message {ClickMessage}");
    }

    private void OnProperty(string name, object? value)
    {
        var s = State;
        switch (name)
        {
            case "time-pos": s.Position = value as double? ?? 0; break;
            case "duration": s.Duration = value as double? ?? 0; break;
            case "volume": s.Volume = value is double v ? VolumeCurve.FromMpv(v) : s.Volume; break;
            case "cache-buffering-state": s.BufferingPercent = value as double?; break;
            case "demuxer-cache-time": s.CacheTime = value as double?; break;
            case "container-fps": s.ContainerFps = value as double?; break;
            case "estimated-vf-fps": s.OutputFps = value as double?; break;
            case "pause": s.IsPaused = value as bool? ?? false; break;
            case "mute": s.IsMuted = value as bool? ?? false; break;
            case "paused-for-cache":
                var buffering = value as bool? ?? false;
                if (buffering && !s.IsBuffering && _streaming) GrowStreamBuffer();
                s.IsBuffering = buffering;
                break;
            case "eof-reached": s.EofReached = value as bool? ?? false; break;
            case "idle-active": s.IsIdle = value as bool? ?? true; break;
            case "seeking": s.IsSeeking = value as bool? ?? false; break;
            case "video-frame-info/interlaced":
                // Latched per file: once the deinterlacer runs, the frames it outputs are
                // progressive, and un-latching would switch it straight off again.
                if (value as bool? == true && !_sourceInterlaced)
                {
                    _sourceInterlaced = true;
                    s.SourceInterlaced = true;
                    if (Settings.Deinterlace == DeinterlaceMode.Auto) ApplyDeinterlace();
                }
                break;
            case "audio-params/channel-count":
                s.SourceChannels = ToInt(value);
                ApplyAudioChannels(); // the downmixes depend on what the source has
                break;
            case "audio-out-params/channel-count": s.OutputChannels = ToInt(value); break;
            case "video-params/w": s.VideoWidth = ToInt(value); break;
            case "video-params/h":
                s.VideoHeight = ToInt(value);
                // Auto picks the profile from the video's size: Balanced for 4K.
                if (Settings.RenderQuality == RenderQuality.Auto) ApplyRenderQuality();
                break;
            case "track-list/count": RefreshTracks(); break;
            case "chapter": s.Chapter = ToInt(value); break;
            case "chapter-list/count" or "current-edition": RefreshChapters(); break;
            case "media-title": s.Title = value as string; break;
            case "path": s.Path = value as string; break;
            case "hwdec-current": s.HwdecCurrent = value as string; break;
            case "video-codec": s.VideoCodec = value as string; break;
            case "video-params/gamma": s.Transfer = value as string; break;
            case "video-params/primaries": s.Primaries = value as string; break;
            case "deinterlace": s.Deinterlace = value as string; break;
            case "vf": s.VideoFilters = value as string; break;
            case "video-format": s.VideoFormat = value as string; break;
            case "current-vo": s.CurrentVo = value as string; break;
            default: return;
        }
        StateChanged?.Invoke(name);
    }

    private static int? ToInt(object? v) => v is long l ? (int)l : null;

    // ---- Commands -------------------------------------------------------------------------

    /// <param name="streaming">Torrent, MEGA or web stream: buffer in bigger chunks before playing.</param>
    public void Open(string pathOrUrl, string? displayTitle = null, bool streaming = false)
    {
        _streaming = streaming;
        _stalls = 0;
        if (displayTitle is not null) _mpv.SetProperty("force-media-title", displayTitle);
        else _mpv.SetProperty("force-media-title", "");
        if (streaming) LoadWithOptions(pathOrUrl, MpvOptionsBuilder.StreamingFileOptions);
        else _mpv.Command("loadfile", pathOrUrl, "replace");
        _mpv.SetProperty("pause", false);
    }

    /// <summary>
    /// Plays a DVD (VIDEO_TS) or Blu-ray (BDMV) folder from its main title, the longest one.
    /// Throws <see cref="MpvException"/> if this libmpv was built without DVD or Blu-ray support.
    /// </summary>
    public void OpenDisc(string root, bool bluray)
    {
        _streaming = false;
        _stalls = 0;
        _mpv.SetProperty(bluray ? "bluray-device" : "dvd-device", root);
        _mpv.SetProperty("force-media-title", Path.GetFileName(root));
        _mpv.Command("loadfile", bluray ? "bd://" : "dvd://", "replace");
        _mpv.SetProperty("pause", false);
    }

    /// <summary>
    /// mpv 0.38 put a playlist index before loadfile's per-file options; 0.37 and older take
    /// the options third, and reject the index as an invalid parameter.
    /// </summary>
    private void LoadWithOptions(string pathOrUrl, string options)
    {
        try { _mpv.Command("loadfile", pathOrUrl, "replace", "-1", options); }
        catch (MpvException e) when (e.Code == MpvException.InvalidParameter)
        {
            _mpv.Command("loadfile", pathOrUrl, "replace", options);
        }
    }

    public void Stop() => _mpv.TryCommand("stop");
    public void TogglePause() => _mpv.TryCommand("cycle", "pause");
    public void SetPaused(bool paused) => _mpv.SetProperty("pause", paused);

    public void SeekRelative(double seconds) =>
        _mpv.TryCommand("seek", seconds.ToString(CultureInfo.InvariantCulture), "relative+exact");

    public void SeekAbsolute(double seconds) =>
        _mpv.TryCommand("seek", Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture), "absolute+exact");

    public void SeekPercent(double percent) =>
        _mpv.TryCommand("seek", Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture), "absolute-percent+exact");

    /// <summary>Advances exactly one frame (pauses playback).</summary>
    public void FrameStep() => _mpv.TryCommand("frame-step");

    /// <summary>Goes back exactly one frame (pauses playback).</summary>
    public void FrameBackStep() => _mpv.TryCommand("frame-back-step");

    /// <param name="level">Slider position 0-150, logarithmic (see <see cref="VolumeCurve"/>).</param>
    public void SetVolume(double level)
    {
        Settings.Volume = Math.Clamp(level, 0, VolumeCurve.Max);
        _mpv.SetProperty("volume", VolumeCurve.ToMpv(Settings.Volume));
    }

    public void ChangeVolume(double delta) => SetVolume(Settings.Volume + delta);

    public void ToggleMute()
    {
        Settings.Muted = !(_mpv.GetFlag("mute") ?? Settings.Muted);
        _mpv.SetProperty("mute", Settings.Muted);
    }

    /// <summary>Hides the pointer over the video while the controls are hidden.</summary>
    public void SetCursorHidden(bool hidden)
    {
        try { _mpv.SetProperty("cursor-autohide", hidden ? "always" : "no"); }
        catch (MpvException) { }
    }

    public void ShowText(string text, int durationMs = 1500) =>
        _mpv.TryCommand("show-text", text, durationMs.ToString(CultureInfo.InvariantCulture));

    public void ToggleStats() => _mpv.TryCommand("script-binding", "stats/display-stats-toggle");

    /// <summary>
    /// Saves an uncompressed PNG of the current frame at the video's native resolution.
    /// Returns the file path.
    /// </summary>
    public string Screenshot(bool includeSubtitles)
    {
        Directory.CreateDirectory(_paths.Screenshots);
        var file = Path.Combine(_paths.Screenshots, ScreenshotFileName(State.Title, State.Position, DateTime.Now));
        _mpv.Command("screenshot-to-file", file, includeSubtitles ? "subtitles" : "video");
        return file;
    }

    internal static string ScreenshotFileName(string? title, double position, DateTime now)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "screenshot" : title!;
        foreach (var c in Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']))
            name = name.Replace(c, '_');
        if (name.Length > 80) name = name[..80];
        var t = TimeSpan.FromSeconds(Math.Max(0, position));
        return $"{name}_{(int)t.TotalHours:00}-{t.Minutes:00}-{t.Seconds:00}.{t.Milliseconds:000}_{now:yyyyMMdd-HHmmss}.png";
    }

    // ---- Live settings --------------------------------------------------------------------

    public void ApplyHdr() => ApplyGroup(MpvOptionsBuilder.Hdr(Settings, RenderProfile, DisplayIsHdr));

    /// <summary>Whether the window's display is in HDR mode; HdrMode.Auto follows it.</summary>
    public bool DisplayIsHdr { get; private set; }

    public void SetDisplayHdr(bool isHdr)
    {
        if (DisplayIsHdr == isHdr) return;
        DisplayIsHdr = isHdr;
        ApplyHdr();
    }
    public void ApplyAudio()
    {
        ApplyGroup(MpvOptionsBuilder.Audio(Settings, State.SourceChannels));
        _audioChannels = MpvOptionsBuilder.AudioChannels(Settings, State.SourceChannels);
    }

    private string? _audioChannels;

    /// <summary>Re-picks the output layout when the source's channel count becomes known.</summary>
    private void ApplyAudioChannels()
    {
        var channels = MpvOptionsBuilder.AudioChannels(Settings, State.SourceChannels);
        if (channels == _audioChannels) return;
        if (_mpv.TrySetProperty("audio-channels", channels)) _audioChannels = channels;
    }

    /// <summary>Applies to the next web link opened.</summary>
    public void ApplyStreamQuality() => _mpv.TrySetProperty("ytdl-format", YtDlp.Format(Settings.StreamMaxHeight));

    public void ApplyDisplayFps() =>
        _mpv.TrySetProperty("display-fps-override", Settings.DisplayFps.ToString(CultureInfo.InvariantCulture));
    private bool _streaming;
    private int _stalls;

    /// <summary>Seconds of video a stream waits for before resuming after a stall.</summary>
    public double StreamBufferSeconds { get; private set; } = MpvOptionsBuilder.InitialStreamBufferSeconds;

    internal void GrowStreamBuffer()
    {
        _stalls++;
        StreamBufferSeconds = MpvOptionsBuilder.StreamBufferAfterStalls(_stalls);
        try { _mpv.SetProperty("cache-pause-wait", StreamBufferSeconds); }
        catch (MpvException) { }
    }

    /// <summary>Reads the counters <see cref="PlaybackHealth"/> works from.</summary>
    public HealthSample SampleHealth() => new(
        DateTime.UtcNow,
        Playing: !State.IsIdle && !State.IsPaused && !State.IsBuffering && !State.IsSeeking,
        Buffering: State.IsBuffering,
        FrameDrops: _mpv.GetLong("frame-drop-count"),
        DecoderDrops: _mpv.GetLong("decoder-frame-drop-count"),
        DelayedFrames: _mpv.GetLong("vo-delayed-frame-count"),
        InputBytesPerSecond: _mpv.GetDouble("cache-speed"),
        CachedSeconds: _mpv.GetDouble("demuxer-cache-duration"),
        FileSize: _mpv.GetLong("file-size"),
        Duration: State.Duration);

    /// <summary>The rendering pipeline in use: chosen in settings, or picked by Auto.</summary>
    public RenderProfile RenderProfile { get; private set; }

    /// <summary>Set when Auto saw frames drop; from then on this session stays on Fast.</summary>
    private bool _autoFellBack;

    public event Action<RenderProfile>? RenderProfileChanged;
    private readonly object _renderLock = new();

    public void SetRenderProfile(RenderProfile profile)
    {
        lock (_renderLock)
        {
            if (profile == RenderProfile) return;
            // Apply before publishing, so anyone who sees the new profile also sees its options.
            ApplyGroup(MpvOptionsBuilder.Scaling(profile));
            ApplyGroup(MpvOptionsBuilder.Hdr(Settings, profile, DisplayIsHdr));
            RenderProfile = profile;
        }
        RenderProfileChanged?.Invoke(profile);
    }

    /// <summary>Auto: frames are dropping, so switch to Fast for the rest of the session.</summary>
    public void FallBackToFastRendering()
    {
        _autoFellBack = true;
        SetRenderProfile(RenderProfile.Fast);
    }

    /// <summary>Re-evaluates the profile after the setting or the video's size changed.</summary>
    public void ApplyRenderQuality()
    {
        SetRenderProfile(Settings.RenderQuality switch
        {
            RenderQuality.High => RenderProfile.High,
            RenderQuality.Balanced => RenderProfile.Balanced,
            RenderQuality.Fast => RenderProfile.Fast,
            _ when _autoFellBack => RenderProfile.Fast,
            _ => MpvOptionsBuilder.AutoProfileFor(State.VideoWidth, State.VideoHeight),
        });
    }

    private readonly object _deinterlaceLock = new();
    private bool _sourceInterlaced;
    private string? _deinterlaceFilter;
    private string? _hwdec;

    /// <summary>
    /// Inserts or removes our deinterlacing filter. Auto follows the frames' own interlaced
    /// flag; the filter is always BWDIF, never the GPU's own deinterlacer.
    /// </summary>
    public void ApplyDeinterlace()
    {
        lock (_deinterlaceLock)
        {
            var wanted = Settings.Deinterlace switch
            {
                DeinterlaceMode.On => true,
                DeinterlaceMode.Off => false,
                _ => _sourceInterlaced,
            };
            var filter = wanted ? MpvOptionsBuilder.DeinterlaceFilter : null;
            if (filter == _deinterlaceFilter) return;

            if (_deinterlaceFilter is not null) _mpv.TryCommand("vf", "remove", MpvOptionsBuilder.DeinterlaceLabel);
            // Software filters need decoded frames in system memory, so hardware decoding
            // switches to its copy-back mode while one is active (interlaced video is at most
            // 1080i, so the copy is cheap).
            SetHwdec(filter is not null);
            if (filter is not null && !_mpv.TryCommand("vf", "add", filter)) filter = null;

            _deinterlaceFilter = filter;
            State.DeinterlaceActive = filter is not null;
            State.DeinterlaceFilter = filter is null ? null : "BWDIF";
        }
        StateChanged?.Invoke("deinterlace-active");
    }

    /// <summary>Whether this file gets hardware decoding (see MpvOptionsBuilder.UseHardwareDecoding).</summary>
    private bool _hardwareWanted;

    private void DecideHardwareDecoding()
    {
        _hardwareWanted = MpvOptionsBuilder.UseHardwareDecoding(
            _mpv.GetString("current-tracks/video/codec"),
            ToInt(_mpv.GetLong("current-tracks/video/demux-w")),
            ToInt(_mpv.GetLong("current-tracks/video/demux-h")));
        ApplyHardwareDecoding();
    }

    private void SetHwdec(bool copyBack)
    {
        var mode = !Settings.HardwareDecoding || !_hardwareWanted ? "no" : copyBack ? "auto-copy-safe" : "auto-safe";
        if (mode == _hwdec) return;
        try { _mpv.SetProperty("hwdec", mode); _hwdec = mode; }
        catch (MpvException) { }
    }

    public void ApplySubtitles() => ApplyGroup(MpvOptionsBuilder.Subtitles(Settings));

    public void ApplyHardwareDecoding()
    {
        lock (_deinterlaceLock) SetHwdec(_deinterlaceFilter is not null);
    }

    private void ApplyGroup(IEnumerable<KeyValuePair<string, string>> options)
    {
        foreach (var (k, v) in options)
        {
            try { _mpv.SetProperty(k, v); }
            catch (MpvException) { _mpv.TrySetOption(k, v); }
        }
    }

    public void SetDownmix(bool stereo)
    {
        Settings.DownmixToStereo = stereo;
        ApplyAudio();
    }

    public void SetHdrMode(HdrMode mode)
    {
        Settings.HdrMode = mode;
        ApplyHdr();
    }

    public void SetDeinterlace(DeinterlaceMode mode)
    {
        Settings.Deinterlace = mode;
        ApplyDeinterlace();
    }

    // ---- Chapters and disc titles ---------------------------------------------------------

    public void RefreshChapters()
    {
        var chapters = new List<ChapterInfo>();
        for (var i = 0; i < (_mpv.GetLong("chapter-list/count") ?? 0); i++)
        {
            var title = _mpv.GetString($"chapter-list/{i}/title");
            chapters.Add(new ChapterInfo(i, string.IsNullOrWhiteSpace(title) ? $"Chapter {i + 1}" : title,
                _mpv.GetDouble($"chapter-list/{i}/time") ?? 0));
        }
        var current = _mpv.GetLong("current-edition");
        var titles = new List<DiscTitleInfo>();
        for (var i = 0; i < (_mpv.GetLong("edition-list/count") ?? 0); i++)
        {
            var id = (int)(_mpv.GetLong($"edition-list/{i}/id") ?? i);
            titles.Add(new DiscTitleInfo(id, _mpv.GetString($"edition-list/{i}/title") ?? $"Title {id + 1}", id == current));
        }
        Chapters = chapters;
        Titles = titles;
        ChaptersChanged?.Invoke();
    }

    public void GoToChapter(int index) => _mpv.TrySetProperty("chapter", index.ToString(CultureInfo.InvariantCulture));

    /// <summary>Next (+1) or previous (-1) chapter; at the first chapter, back to its start.</summary>
    public void StepChapter(int delta) => _mpv.TryCommand("add", "chapter", delta.ToString(CultureInfo.InvariantCulture));

    /// <summary>Plays another title of the disc (an mpv edition).</summary>
    public void SelectTitle(int id) => _mpv.TrySetProperty("edition", id.ToString(CultureInfo.InvariantCulture));

    // ---- Tracks ---------------------------------------------------------------------------

    public void RefreshTracks()
    {
        var count = (int)(_mpv.GetLong("track-list/count") ?? 0);
        var list = new List<TrackInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var p = $"track-list/{i}/";
            var type = _mpv.GetString(p + "type") switch
            {
                "video" => TrackType.Video,
                "audio" => TrackType.Audio,
                "sub" => TrackType.Subtitle,
                _ => (TrackType?)null,
            };
            if (type is null) continue;
            var ch = _mpv.GetLong(p + "demux-channel-count");
            list.Add(new TrackInfo(
                (int)(_mpv.GetLong(p + "id") ?? i),
                type.Value,
                _mpv.GetString(p + "title"),
                _mpv.GetString(p + "lang"),
                _mpv.GetString(p + "codec"),
                _mpv.GetFlag(p + "selected") ?? false,
                _mpv.GetFlag(p + "external") ?? false,
                _mpv.GetFlag(p + "default") ?? false,
                ch is null ? null : (int)ch));
        }
        Tracks = list;
        TracksChanged?.Invoke();
    }

    public void SelectTrack(TrackType type, int? id)
    {
        var prop = type switch { TrackType.Video => "vid", TrackType.Audio => "aid", _ => "sid" };
        _mpv.SetProperty(prop, id?.ToString(CultureInfo.InvariantCulture) ?? "no");
        RefreshTracks();
    }

    public void CycleTrack(TrackType type)
    {
        _mpv.TryCommand("cycle", type == TrackType.Audio ? "audio" : "sub");
        RefreshTracks();
    }

    public void AddSubtitle(string path)
    {
        _mpv.Command("sub-add", path, "select");
        RefreshTracks();
    }

    public void ToggleSubtitleVisibility() => _mpv.TryCommand("cycle", "sub-visibility");
    /// <summary>Shifts subtitle timing; returns the total shift now in effect, in seconds.</summary>
    public double AdjustSubtitleDelay(double seconds)
    {
        _mpv.TryCommand("add", "sub-delay", seconds.ToString(CultureInfo.InvariantCulture));
        return Math.Round(_mpv.GetDouble("sub-delay") ?? 0, 3);
    }

    public void Dispose() => _mpv.Dispose();
}
