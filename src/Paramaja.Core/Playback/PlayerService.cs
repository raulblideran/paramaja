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
        _hwdec = "no"; // decided per file, once its codec and size are known (DecideHardwareDecoding)
        RenderProfile = MpvOptionsBuilder.InitialProfile(settings.RenderQuality);
        foreach (var (k, v) in MpvOptionsBuilder.Build(settings, paths, windowId))
        {
            try { _mpv.SetOption(k, v); }
            catch (MpvException e) when (e.Code == MpvException.OptionNotFound && MpvOptionsBuilder.OptionalOptions.Contains(k)) { }
        }
        foreach (var (k, v) in extraOptions ?? [])
        {
            try { _mpv.SetOption(k, v); }
            catch (MpvException e) when (e.Code == MpvException.OptionNotFound && MpvOptionsBuilder.OptionalOptions.Contains(k)) { }
        }
        _audioFilters = MpvOptionsBuilder.AudioFilters(settings, null);
        Wire();
        _mpv.Initialize("info"); // info and up go into the log (Create Log), not just warnings
        _osdMargin = _mpv.GetLong("osd-margin-y") ?? 16;
        _sacdReadable = Sacd.SacdProtocol.Register(_mpv);
        _originalAspect = _mpv.GetDouble("video-aspect-override") ?? -2;
        Observe();
        _mpv.AddHook("on_preloaded", OnPreloaded);
        BindKeys();
    }

    private void Wire()
    {
        _mpv.PropertyChanged += OnProperty;
        _mpv.FileLoaded += () =>
        {
            RefreshTracks();
            RefreshChapters();
            ReadStreamHeights();
            FileLoaded?.Invoke();
        };
        _mpv.StartFile += () =>
        {
            // Every file is judged on its own frames, and the switches made for the previous one
            // (deinterlacing, hardware decoding, a stereo channel) don't carry over.
            lock (_deinterlaceLock)
            {
                _file++;
                _probe?.Cancel();
                _probe = null;
                _sourceInterlaced = false;
                _interlaceBy = null;
                _fieldOrder = null;
                _deinterlaceOff = false;
            }
            ResetHardwareDecoding();
            ApplyDeinterlace();
            SetStereoChannel(StereoChannel.Both);
            State.HasVideo = null;
            State.DisplayAspect = null;
            StateChanged?.Invoke("has-video");
            SetPictureFit(_keepFit);
            _keepFit = PictureFit.Original;
        };
        // The interlace flag and the channel counts are watched, but mpv reports only changes, and
        // between two videos alike (two interlaced, two stereo) its last word could be the gap
        // between them ("none") or nothing at all. So they're also read whenever the audio is set
        // up and playback (re)starts.
        _mpv.PlaybackRestart += () =>
        {
            if (_mpv.GetFlag("video-frame-info/interlaced") == true) MarkInterlaced(PlayerState.ByFlags, null);
            ReadChannelsLive();
        };
        _mpv.AudioReconfig += ReadChannelsLive;
        _mpv.EndFile += (reason, err) =>
            EndFile?.Invoke(reason, reason == EndFileReason.Error ? MpvClient.ErrorString(err) : null);
        _mpv.ClientMessage += args =>
        {
            if (args.Length >= 2 && args[0] == KeyMessage) KeyPressed?.Invoke(args[1]);
            else if (args.Length >= 1 && args[0] == MouseMessage) MouseMoved?.Invoke();
            else if (args.Length >= 1 && args[0] == ClickMessage) MouseClicked?.Invoke();
        };
        _mpv.LogMessage += m =>
        {
            _messages.Add($"[{m.Prefix}] {m.Level}: {m.Text}");
            Log?.Invoke(m);
        };
    }

    private readonly RecentLines _messages = new();

    public IReadOnlyList<string> RecentMessages() => _messages.ToList();

    private void Observe()
    {
        foreach (var p in new[] { "time-pos", "duration", "volume", "cache-buffering-state", "demuxer-cache-time",
                     "container-fps", "estimated-vf-fps", "video-params/aspect" })
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
    /// reliably generated for an embedded window. The app reads clicks from the OS (VideoClicks:
    /// a mouse hook on Windows, XInput2 on Linux), as mpv's window doesn't reliably report them;
    /// mpv's own report is the fallback on Linux, where XInput2 may be missing.
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
            case "video-params/aspect":
                // The source's own shape: with the picture stretched to 4:3 mpv reports 4:3.
                if (s.PictureFit == PictureFit.Resize4x3) return;
                s.DisplayAspect = value as double?;
                if (s.PictureFit == PictureFit.Crop16x9) ApplyCrop(); // the crop needs the shape
                break;
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
                if (value as bool? == true) MarkInterlaced(PlayerState.ByFlags, null);
                break;
            case "audio-params/channel-count":
                s.SourceChannels = ToInt(value);
                // The downmix and the stereo channel choice depend on what the source has.
                ApplyAudioChannels();
                ApplyAudioFilters();
                break;
            case "audio-out-params/channel-count": s.OutputChannels = ToInt(value); break;
            case "video-params/w":
                s.VideoWidth = ToInt(value);
                if (s.PictureFit == PictureFit.Crop16x9) ApplyCrop();
                break;
            case "video-params/h":
                s.VideoHeight = ToInt(value);
                if (s.PictureFit == PictureFit.Crop16x9) ApplyCrop();
                // Auto picks the profile from the video's size: Balanced for 4K.
                if (Settings.RenderQuality == RenderQuality.Auto) ApplyRenderQuality();
                break;
            case "track-list/count": RefreshTracks(); break;
            case "chapter": s.Chapter = ToInt(value); break;
            case "chapter-list/count" or "current-edition": RefreshChapters(); break;
            case "media-title": s.Title = value as string; break;
            case "path": s.Path = value as string; break;
            case "hwdec-current":
                s.HwdecCurrent = value as string;
                CheckHardwareDecoding();
                break;
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

    /// <summary>The source's and the output's channel counts and the duration, asked live; a change goes through as an event would.</summary>
    private void ReadChannelsLive()
    {
        foreach (var name in new[] { "audio-params/channel-count", "audio-out-params/channel-count" })
        {
            var live = _mpv.GetLong(name);
            var known = name.StartsWith("audio-params") ? State.SourceChannels : State.OutputChannels;
            if (ToInt(live) != known) OnProperty(name, live);
        }
        if (_mpv.GetDouble("duration") is { } duration && Math.Abs(duration - State.Duration) > 0.001) OnProperty("duration", duration);
    }

    // ---- Commands -------------------------------------------------------------------------

    /// <param name="streaming">Torrent, MEGA or web stream: buffer in bigger chunks before playing.</param>
    public void Open(string pathOrUrl, string? displayTitle = null, bool streaming = false)
    {
        _streaming = streaming;
        _stalls = 0;
        ForgetSacd();
        // mpv's yt-dlp hook reads ytdl-format as each link loads: YouTube's up to the streaming
        // quality chosen, any other site's at its best.
        _mpv.TrySetProperty("ytdl-format", YtDlp.FormatFor(pathOrUrl, Settings.StreamMaxHeight));
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
        ForgetSacd();
        _mpv.SetProperty(bluray ? "bluray-device" : "dvd-device", root);
        _mpv.SetProperty("force-media-title", DiscTitle(root));
        _mpv.Command("loadfile", bluray ? "bd://" : "dvd://", "replace");
        _mpv.SetProperty("pause", false);
    }

    /// <summary>
    /// An Audio CD, for builds without libVLC (which normally plays CDs). Throws
    /// <see cref="MpvException"/> if this libmpv was built without CD support.
    /// </summary>
    public void OpenAudioCd(string device)
    {
        _streaming = false;
        _stalls = 0;
        ForgetSacd();
        _mpv.SetProperty("cdda-device", device);
        _mpv.SetProperty("force-media-title", "Audio CD");
        _mpv.Command("loadfile", "cdda://", "replace");
        _mpv.SetProperty("pause", false);
    }

    /// <summary>A disc's name: its folder's, or an image's file name without the extension.</summary>
    internal static string DiscTitle(string root)
    {
        var trimmed = root.TrimEnd('/', '\\');
        if (File.Exists(trimmed)) return Path.GetFileNameWithoutExtension(trimmed);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : root;
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

    public bool? QueryPaused() => _mpv.GetFlag("pause");

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
        ApplyGroup(MpvOptionsBuilder.Audio(Settings, State.SourceChannels, _stereoChannel));
        _audioChannels = MpvOptionsBuilder.AudioChannels(Settings, State.SourceChannels);
        _audioFilters = MpvOptionsBuilder.AudioFilters(Settings, State.SourceChannels, _stereoChannel);
    }

    private string? _audioChannels;
    private string? _audioFilters;
    private StereoChannel _stereoChannel;

    /// <summary>One channel of a stereo source on both speakers, for the playing video only.</summary>
    public void SetStereoChannel(StereoChannel channel)
    {
        _stereoChannel = channel;
        State.StereoChannel = channel;
        ApplyAudioFilters();
        StateChanged?.Invoke("stereo-channel");
    }

    private void ApplyAudioFilters()
    {
        var af = MpvOptionsBuilder.AudioFilters(Settings, State.SourceChannels, _stereoChannel);
        if (af == _audioFilters) return;
        if (_mpv.TrySetProperty("af", af)) _audioFilters = af;
    }

    /// <summary>Re-picks the output layout when the source's channel count becomes known.</summary>
    private void ApplyAudioChannels()
    {
        var channels = MpvOptionsBuilder.AudioChannels(Settings, State.SourceChannels);
        if (channels == _audioChannels) return;
        if (_mpv.TrySetProperty("audio-channels", channels)) _audioChannels = channels;
    }

    /// <summary>Applies to the next YouTube link opened (each link sets its format as it opens).</summary>
    public void ApplyStreamQuality() { }

    /// <summary>
    /// The qualities of a YouTube video (the only site the quality options are for), from what
    /// mpv's yt-dlp hook got for the page (it keeps yt-dlp's answer in user-data until the file
    /// ends). Nothing runs yt-dlp a second time, and a direct link (a file on a web server, MEGA's
    /// desktop app streaming) is never handed to it: MEGA's local server stalled when two clients
    /// read the same file at once.
    /// </summary>
    private void ReadStreamHeights()
    {
        var heights = HeightsFromYtdlHook(_mpv.GetString("path"), _mpv.GetString("user-data/mpv/ytdl/json-subprocess-result/stdout"));
        if (heights.SequenceEqual(State.StreamHeights)) return;
        State.StreamHeights = heights;
        StateChanged?.Invoke("stream-heights");
    }

    /// <summary>
    /// The heights in yt-dlp's -J output as the hook kept it, for a YouTube <paramref name="page"/>
    /// only; a string inside user-data comes back as a JSON string (quoted, escaped).
    /// </summary>
    internal static IReadOnlyList<int> HeightsFromYtdlHook(string? page, string? stdout)
    {
        if (string.IsNullOrEmpty(stdout) || !YtDlp.IsYouTube(page)) return [];
        try
        {
            var json = stdout.StartsWith('"') ? System.Text.Json.JsonSerializer.Deserialize<string>(stdout) ?? "" : stdout;
            return json.Length == 0 ? [] : YtDlp.ParseInfo(json).Heights;
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    /// <summary>
    /// The page is opened again with the new format: mpv's yt-dlp hook reads ytdl-format as each
    /// link loads. It starts where it was, paused or not as it was, with the picture as it was.
    /// </summary>
    public void ReloadStream(int maxHeight)
    {
        if (_mpv.GetString("path") is not { Length: > 0 } url || !YtDlp.IsYouTube(url)) return;
        var position = State.Position;
        _mpv.TrySetProperty("ytdl-format", YtDlp.Format(maxHeight));
        _keepFit = State.PictureFit;
        _stalls = 0;
        LoadWithOptions(url, MpvOptionsBuilder.StreamingFileOptions
                             + ",start=" + position.ToString("0.###", CultureInfo.InvariantCulture));
    }

    // ---- On-screen messages and the picture's shape ---------------------------------------------

    /// <summary>mpv's own margin above its messages, in its scaled pixels (720 to the window's height).</summary>
    private readonly long _osdMargin;

    public void SetOsdTopInset(double fraction) =>
        _mpv.TrySetProperty("osd-margin-y", MpvOptionsBuilder.OsdMarginBelow(fraction, _osdMargin).ToString(CultureInfo.InvariantCulture));

    /// <summary>mpv's default for video-aspect-override (the video's own shape), to go back to.</summary>
    private readonly double _originalAspect;
    /// <summary>Carried over a reload of the same video at another quality.</summary>
    private PictureFit _keepFit;

    public void SetPictureFit(PictureFit fit)
    {
        State.PictureFit = fit;
        _mpv.TrySetProperty("video-aspect-override", fit == PictureFit.Resize4x3
            ? "4:3"
            : _originalAspect.ToString(CultureInfo.InvariantCulture));
        ApplyCrop();
        StateChanged?.Invoke("picture-fit");
    }

    /// <summary>The 16:9 crop in the source's pixels; needs the video's size and shape, so it's applied again once they're known.</summary>
    private void ApplyCrop()
    {
        var crop = State.PictureFit == PictureFit.Crop16x9
            ? MpvOptionsBuilder.CropTo(State.VideoWidth, State.VideoHeight, State.DisplayAspect, 16.0 / 9) ?? ""
            : "";
        // video-crop is mpv 0.38+; older ones simply don't crop.
        _mpv.TrySetProperty("video-crop", crop);
    }

    public void ApplyTiming() => ApplyGroup(MpvOptionsBuilder.Timing(Settings));
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
        Duration: State.Duration)
    {
        Position = State.Position,
        OutputFps = _mpv.GetDouble("estimated-vf-fps"),
        DisplayFps = _mpv.GetDouble("display-fps"),
        MeasuredDisplayFps = _mpv.GetDouble("estimated-display-fps"),
        VsyncJitter = _mpv.GetDouble("vsync-jitter"),
        AvSync = _mpv.GetDouble("avsync"),
        MistimedFrames = _mpv.GetLong("mistimed-frame-count"),
        GpuMicroseconds = GpuFrameTime(),
    };

    /// <summary>
    /// The GPU's average time for a new frame, over all its render passes, in microseconds.
    /// mpv gives them as one node (vo-passes: {"fresh": [{"desc", "avg", …}, …]}, in ns).
    /// </summary>
    private double? GpuFrameTime() => GpuFrameTime(_mpv.GetString("vo-passes"));

    internal static double? GpuFrameTime(string? passes)
    {
        if (string.IsNullOrEmpty(passes)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(passes);
            if (!doc.RootElement.TryGetProperty("fresh", out var fresh) || fresh.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            long total = 0;
            foreach (var pass in fresh.EnumerateArray())
                if (pass.TryGetProperty("avg", out var avg) && avg.TryGetInt64(out var ns)) total += ns;
            return total > 0 ? total / 1000.0 : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>What the log (Create Log) says about mpv and how it's playing the video.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Details()
    {
        string V(string property) => _mpv.GetString(property) is { Length: > 0 } v ? v : "n/a";
        return
        [
            new("Versions", $"{V("mpv-version")}, FFmpeg {V("ffmpeg-version")}"),
            new("Video output", $"{V("current-vo")}, {V("current-gpu-context")}, gpu-api {V("gpu-api")}, d3d11-adapter {V("d3d11-adapter")}"),
            new("Video codec", $"{V("video-codec")}, profile {V("current-tracks/video/codec-profile")}"),
            new("Pixel format", $"{V("video-params/pixelformat")} (hardware {V("video-params/hw-pixelformat")})"),
            new("Hardware decoding", $"wanted {(_hardwareWanted ? "yes" : "no")}, asked for {_hwdec ?? "n/a"}, in use {V("hwdec-current")}"),
            new("Interlaced", State.SourceInterlaced ? $"yes ({State.InterlaceDetectedBy}), deinterlacing {(State.DeinterlacingOn ? "on" : "switched off")}" : "no"),
            new("Video filters", V("vf")),
            new("Frame timing", $"video-sync {V("video-sync")}, display-fps-override {V("display-fps-override")}, framedrop {V("framedrop")}"),
            new("Audio", $"{V("audio-codec-name")} {V("audio-params/channel-count")}ch → {V("audio-out-params/channel-count")}ch, ao {V("current-ao")}, af {V("af")}"),
        ];
    }

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
    private string? _interlaceBy;
    /// <summary>The picture check's field order; null where the frames' own flags give it.</summary>
    private FieldOrder? _fieldOrder;
    /// <summary>Switched off for this video; every new interlaced video starts with it on.</summary>
    private bool _deinterlaceOff;
    private string? _deinterlaceFilter;
    private string? _hwdec;
    /// <summary>Counts files, so a picture check's late answer can't land on the next one.</summary>
    private int _file;
    private CancellationTokenSource? _probe;

    /// <summary>
    /// Inserts or removes our deinterlacing filter: BWDIF for every interlaced video, unless its
    /// switch is off; never the GPU's own deinterlacer. Publishes the state every time, so a new
    /// file always reports its own.
    /// </summary>
    public void ApplyDeinterlace()
    {
        lock (_deinterlaceLock)
        {
            var filter = _sourceInterlaced && !_deinterlaceOff ? MpvOptionsBuilder.DeinterlaceFilterFor(_fieldOrder) : null;
            if (filter != _deinterlaceFilter)
            {
                if (_deinterlaceFilter is not null) _mpv.TryCommand("vf", "remove", MpvOptionsBuilder.DeinterlaceLabel);
                // Software filters need decoded frames in system memory, so hardware decoding
                // switches to its copy-back mode while one is active (interlaced video is at most
                // 1080i, so the copy is cheap). Progressive video never needs it.
                SetHwdec(filter is not null);
                if (filter is not null && !_mpv.TryCommand("vf", "add", filter)) filter = null;
                _deinterlaceFilter = filter;
            }
            State.SourceInterlaced = _sourceInterlaced;
            State.InterlaceDetectedBy = _sourceInterlaced ? _interlaceBy : null;
            State.DeinterlacingOn = !_deinterlaceOff;
            State.DeinterlaceActive = _deinterlaceFilter is not null;
            State.DeinterlaceFilter = _deinterlaceFilter is null ? null : "BWDIF";
        }
        StateChanged?.Invoke("deinterlace-active");
    }

    /// <summary>The video turned out interlaced: by its frames' flags, or by the picture check.</summary>
    private void MarkInterlaced(string by, FieldOrder? order, int? file = null)
    {
        lock (_deinterlaceLock)
        {
            if (_sourceInterlaced || file is { } f && f != _file) return;
            _sourceInterlaced = true;
            _interlaceBy = by;
            _fieldOrder = order;
        }
        ApplyDeinterlace();
    }

    /// <summary>The video menu's switch, there only for interlaced video; it lasts for that video.</summary>
    public void SetDeinterlacing(bool on)
    {
        lock (_deinterlaceLock)
        {
            if (!_sourceInterlaced || _deinterlaceOff == !on) return;
            _deinterlaceOff = !on;
        }
        ApplyDeinterlace();
    }

    /// <summary>
    /// Frames without interlace flags may still be interlaced: tape captures saved as lossless or
    /// MJPEG AVIs, some rips. Where the CPU decodes anyway (so no copy-back is needed), a second,
    /// headless mpv checks the picture (InterlaceProbe); hardware-decoded video goes by its flags.
    /// </summary>
    private void StartInterlaceProbe((string? Codec, int? Width, int? Height, double? Fps) video)
    {
        if (_streaming || _hardwareWanted || video.Codec is null || !InterlaceVerdict.Applies(video.Height, video.Fps)) return;
        if (_mpv.GetString("path") is not { } path || !File.Exists(path)) return; // local files only
        var start = InterlaceProbe.StartFor(_mpv.GetDouble("duration"));
        CancellationTokenSource cts;
        int file;
        lock (_deinterlaceLock)
        {
            cts = _probe = new CancellationTokenSource();
            file = _file;
        }
        ProbeTask = Task.Run(async () =>
        {
            try
            {
                if (await InterlaceProbe.AnalyzeAsync(path, start, cts.Token) is { } order && !cts.IsCancellationRequested)
                    MarkInterlaced(PlayerState.ByPicture, order, file);
            }
            catch (Exception e) when (e is MpvException or OperationCanceledException or ObjectDisposedException or DllNotFoundException) { }
        });
    }

    /// <summary>The latest picture check; tests wait for it.</summary>
    internal Task ProbeTask { get; private set; } = Task.CompletedTask;

    // ---- Hardware decoding: decided for each video ------------------------------------------

    /// <summary>This video benefits from hardware decoding (see MpvOptionsBuilder.UseHardwareDecoding).</summary>
    private bool _hardwareWanted;
    /// <summary>Switched off for this video; every new video starts with it on.</summary>
    private bool _hardwareOff;
    /// <summary>Whether the GPU decodes this video: null until its decoder has started.</summary>
    private bool? _hardwareWorks;
    /// <summary>When hwdec was last changed: hwdec-current may still report the old decoder briefly.</summary>
    private DateTime _hwdecChangedAt;

    /// <summary>How long after a change a software decoder is taken as the old one, not a fallback.</summary>
    internal static readonly TimeSpan HwdecSettleTime = TimeSpan.FromSeconds(2);

    private void ResetHardwareDecoding()
    {
        _hardwareWanted = false;
        _hardwareOff = false;
        _hardwareWorks = null;
        PublishHardwareDecoding();
    }

    /// <summary>
    /// Runs once the file is open but before its tracks are picked and its decoders start, so
    /// the video decodes the chosen way from its first frame (switching later raced with
    /// interlace detection) and a stereo audio track plays from the start.
    /// </summary>
    private void OnPreloaded()
    {
        var video = VideoTrackToPlay();
        _hardwareWanted = MpvOptionsBuilder.UseHardwareDecoding(video.Codec, video.Width, video.Height);
        lock (_deinterlaceLock) SetHwdec(_deinterlaceFilter is not null);
        PublishHardwareDecoding();
        PickStereoAudio();
        StartInterlaceProbe(video);
    }

    /// <summary>
    /// A stereo track where the file has several audio tracks (concert discs often carry the
    /// stereo mix beside 5.1 and DTS). File-local, so it doesn't carry over to the next file.
    /// </summary>
    private void PickStereoAudio()
    {
        if (MpvOptionsBuilder.StereoTrack(ReadTracks(), Settings.PreferredAudioLanguages) is { } id)
            _mpv.TrySetProperty("file-local-options/aid", id.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The video track mpv is about to play. At on_preloaded it hasn't chosen yet (so
    /// current-tracks is still empty): the file's default video track, else its first, leaving
    /// out cover art.
    /// </summary>
    private (string? Codec, int? Width, int? Height, double? Fps) VideoTrackToPlay()
    {
        var count = _mpv.GetLong("track-list/count") ?? 0;
        int? pick = null;
        for (var i = 0; i < count; i++)
        {
            var p = $"track-list/{i}/";
            if (_mpv.GetString(p + "type") != "video" || _mpv.GetFlag(p + "albumart") == true) continue;
            if (_mpv.GetFlag(p + "default") == true) { pick = i; break; }
            pick ??= i;
        }
        if (pick is not { } t) return (null, null, null, null);
        var track = $"track-list/{t}/";
        return (_mpv.GetString(track + "codec"), ToInt(_mpv.GetLong(track + "demux-w")), ToInt(_mpv.GetLong(track + "demux-h")),
            _mpv.GetDouble(track + "demux-fps"));
    }

    /// <summary>
    /// The decoder started (or changed): the first answer says whether the GPU takes this video
    /// at all (mpv's "safe" list leaves out what GPUs decode badly, and falls back to software
    /// for what this GPU can't). A later fall back to software, mid-video, counts too.
    /// </summary>
    private void CheckHardwareDecoding()
    {
        if (!_hardwareWanted || _hardwareOff || _hwdec is null or "no") return;
        var current = _mpv.GetString("hwdec-current"); // the live value; events can lag behind
        if (string.IsNullOrEmpty(current)) return;     // no video decoder yet
        bool works = current != "no";
        if (_hardwareWorks == works) return;
        if (!works && _hardwareWorks == true && DateTime.UtcNow - _hwdecChangedAt < HwdecSettleTime) return;
        _hardwareWorks = works;
        PublishHardwareDecoding();
    }

    private void PublishHardwareDecoding()
    {
        State.HardwareDecodingApplies = _hardwareWanted && _hardwareWorks != false;
        State.HardwareDecodingOn = State.HardwareDecodingApplies && !_hardwareOff;
        StateChanged?.Invoke("hardware-decoding");
    }

    public void SetHardwareDecoding(bool on)
    {
        if (!State.HardwareDecodingApplies || _hardwareOff == !on) return;
        _hardwareOff = !on;
        lock (_deinterlaceLock) SetHwdec(_deinterlaceFilter is not null);
        PublishHardwareDecoding();
    }

    private void SetHwdec(bool copyBack)
    {
        var mode = !_hardwareWanted || _hardwareOff || _hardwareWorks == false ? "no" : copyBack ? "auto-copy-safe" : "auto-safe";
        if (mode == _hwdec) return;
        try
        {
            _mpv.SetProperty("hwdec", mode);
            _hwdec = mode;
            _hwdecChangedAt = DateTime.UtcNow;
        }
        catch (MpvException) { }
    }

    public void ApplySubtitles() => ApplyGroup(MpvOptionsBuilder.Subtitles(Settings));

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
        // An SACD's areas (stereo, multichannel) are its titles.
        if (_sacd is { Areas.Count: > 1 } sacd)
            titles = sacd.Areas.Select(a => new DiscTitleInfo((int)a.Kind,
                $"{a.Label} ({TimeFormat.Format(a.Tracks.Sum(t => t.Seconds))})", a.Kind == _sacdArea)).ToList();
        Chapters = chapters;
        Titles = titles;
        ChaptersChanged?.Invoke();
    }

    public void GoToChapter(int index) => _mpv.TrySetProperty("chapter", index.ToString(CultureInfo.InvariantCulture));

    /// <summary>Next (+1) or previous (-1) chapter; at the first chapter, back to its start.</summary>
    public void StepChapter(int delta) => _mpv.TryCommand("add", "chapter", delta.ToString(CultureInfo.InvariantCulture));

    /// <summary>Plays another title of the disc (an mpv edition), or another area of an SACD.</summary>
    public void SelectTitle(int id)
    {
        if (_sacd is { } sacd) OpenSacdArea(sacd, (Sacd.SacdAreaKind)id);
        else _mpv.TrySetProperty("edition", id.ToString(CultureInfo.InvariantCulture));
    }

    // mpv has no disc menu support; discs with menus play through VlcDiscPlayer.
    public bool HasDiscMenu => false;
    public bool IsInDiscMenu => false;
    public void NavigateMenu(MenuNavigation direction) { }
    public void ShowDiscMenu() { }

    // ---- Tracks ---------------------------------------------------------------------------

    public void RefreshTracks()
    {
        Tracks = ReadTracks();
        TracksChanged?.Invoke();
        // Audio alone, or with its cover art: no picture to step through or reshape.
        bool? hasVideo = Tracks.Count == 0 ? null : Tracks.Any(t => t.Type == TrackType.Video && !t.AlbumArt);
        if (hasVideo == State.HasVideo) return;
        State.HasVideo = hasVideo;
        StateChanged?.Invoke("has-video");
    }

    private List<TrackInfo> ReadTracks()
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
                ch is null ? null : (int)ch,
                _mpv.GetFlag(p + "albumart") ?? false));
        }
        return list;
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

    // ---- SACD images ------------------------------------------------------------------------

    /// <summary>The SACD image playing, and which of its areas; its areas are the titles then.</summary>
    private Sacd.SacdImage? _sacd;
    private Sacd.SacdAreaKind _sacdArea;
    /// <summary>This libmpv reads sacd:// through us (Sacd.SacdProtocol).</summary>
    private readonly bool _sacdReadable;

    /// <summary>
    /// An SACD image: its stereo area (else the multichannel one) as one timeline of its tracks,
    /// each a chapter. Throws <see cref="InvalidDataException"/> or <see cref="IOException"/> if the
    /// image can't be read, and <see cref="MpvException"/> where this libmpv can't take it.
    /// </summary>
    public void OpenSacd(string image)
    {
        if (!_sacdReadable) throw new MpvException("this build's libmpv can't read SACD images", MpvException.Unsupported);
        var sacd = Sacd.SacdProtocol.Image(image);
        OpenSacdArea(sacd, sacd.Areas[0].Kind);
    }

    private void OpenSacdArea(Sacd.SacdImage sacd, Sacd.SacdAreaKind kind)
    {
        if (sacd.Area(kind) is not { } area) return;
        _streaming = false;
        _stalls = 0;
        _sacd = sacd;
        _sacdArea = kind;
        var title = sacd.AlbumTitle ?? Path.GetFileNameWithoutExtension(sacd.Path);
        _mpv.SetProperty("force-media-title", sacd.AlbumArtist is { } artist ? $"{artist} — {title}" : title);
        // mpv lets a timeline (EDL) open only its own protocols, unless told otherwise; for this
        // file only, it may open sacd://, which is ours.
        LoadWithOptions(Sacd.SacdProtocol.EdlFor(sacd, area), "load-unsafe-playlists=yes");
        _mpv.SetProperty("pause", false);
    }

    private void ForgetSacd() => _sacd = null;

    public void Dispose()
    {
        lock (_deinterlaceLock) _probe?.Cancel();
        // The picture check's own mpv closes first: none outlives its player.
        try { ProbeTask.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        _mpv.Dispose();
    }
}
