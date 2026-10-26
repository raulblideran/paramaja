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

    private readonly MpvClient _mpv;
    private readonly PortablePaths _paths;

    public PlayerSettings Settings { get; }
    public PlayerState State { get; } = new();
    public IReadOnlyList<TrackInfo> Tracks { get; private set; } = [];
    public MpvClient Mpv => _mpv;

    public event Action<string>? StateChanged;
    public event Action? TracksChanged;
    public event Action<string>? KeyPressed;
    public event Action? MouseMoved;
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
        foreach (var (k, v) in MpvOptionsBuilder.Build(settings, paths, windowId)) _mpv.SetOption(k, v);
        foreach (var (k, v) in extraOptions ?? []) _mpv.SetOption(k, v);
        Wire();
        _mpv.Initialize();
        Observe();
        BindKeys();
    }

    private void Wire()
    {
        _mpv.PropertyChanged += OnProperty;
        _mpv.FileLoaded += () =>
        {
            RefreshTracks();
            FileLoaded?.Invoke();
        };
        _mpv.EndFile += (reason, err) =>
            EndFile?.Invoke(reason, reason == EndFileReason.Error ? MpvClient.ErrorString(err) : null);
        _mpv.ClientMessage += args =>
        {
            if (args.Length >= 2 && args[0] == KeyMessage) KeyPressed?.Invoke(args[1]);
            else if (args.Length >= 1 && args[0] == MouseMessage) MouseMoved?.Invoke();
        };
        _mpv.LogMessage += m => Log?.Invoke(m);
    }

    private void Observe()
    {
        foreach (var p in new[] { "time-pos", "duration", "volume", "cache-buffering-state", "demuxer-cache-time",
                     "container-fps", "estimated-vf-fps" })
            _mpv.ObserveDouble(p);
        foreach (var p in new[] { "pause", "mute", "paused-for-cache", "eof-reached", "idle-active", "seeking", "deinterlace-active" })
            _mpv.ObserveFlag(p);
        foreach (var p in new[] { "audio-params/channel-count", "audio-out-params/channel-count", "video-params/w",
                     "video-params/h", "track-list/count" })
            _mpv.ObserveLong(p);
        foreach (var p in new[] { "media-title", "path", "hwdec-current", "video-codec", "video-params/gamma",
                     "video-params/primaries", "deinterlace", "vf", "video-format", "current-vo" })
            _mpv.ObserveString(p);
    }

    /// <summary>
    /// When the video surface has focus mpv gets the keystrokes; forward each of our
    /// shortcuts back to us as a client message so handling is identical to the app window.
    /// </summary>
    private void BindKeys()
    {
        foreach (var key in ShortcutMap.Default.Keys)
            _mpv.TryCommand("keybind", key, $"script-message {KeyMessage} \"{key}\"");
        _mpv.TryCommand("keybind", "MOUSE_MOVE", $"script-message {MouseMessage}");
    }

    private void OnProperty(string name, object? value)
    {
        var s = State;
        switch (name)
        {
            case "time-pos": s.Position = value as double? ?? 0; break;
            case "duration": s.Duration = value as double? ?? 0; break;
            case "volume": s.Volume = value as double? ?? s.Volume; break;
            case "cache-buffering-state": s.BufferingPercent = value as double?; break;
            case "demuxer-cache-time": s.CacheTime = value as double?; break;
            case "container-fps": s.ContainerFps = value as double?; break;
            case "estimated-vf-fps": s.OutputFps = value as double?; break;
            case "pause": s.IsPaused = value as bool? ?? false; break;
            case "mute": s.IsMuted = value as bool? ?? false; break;
            case "paused-for-cache": s.IsBuffering = value as bool? ?? false; break;
            case "eof-reached": s.EofReached = value as bool? ?? false; break;
            case "idle-active": s.IsIdle = value as bool? ?? true; break;
            case "seeking": s.IsSeeking = value as bool? ?? false; break;
            case "deinterlace-active": s.DeinterlaceActive = value as bool? ?? false; break;
            case "audio-params/channel-count": s.SourceChannels = ToInt(value); break;
            case "audio-out-params/channel-count": s.OutputChannels = ToInt(value); break;
            case "video-params/w": s.VideoWidth = ToInt(value); break;
            case "video-params/h": s.VideoHeight = ToInt(value); break;
            case "track-list/count": RefreshTracks(); break;
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

    public void Open(string pathOrUrl, string? displayTitle = null)
    {
        if (displayTitle is not null) _mpv.SetProperty("force-media-title", displayTitle);
        else _mpv.SetProperty("force-media-title", "");
        _mpv.Command("loadfile", pathOrUrl, "replace");
        _mpv.SetProperty("pause", false);
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

    public void SetVolume(double volume)
    {
        Settings.Volume = Math.Clamp(volume, 0, 150);
        _mpv.SetProperty("volume", Settings.Volume);
    }

    public void ChangeVolume(double delta) => SetVolume((_mpv.GetDouble("volume") ?? Settings.Volume) + delta);

    public void ToggleMute()
    {
        Settings.Muted = !(_mpv.GetFlag("mute") ?? Settings.Muted);
        _mpv.SetProperty("mute", Settings.Muted);
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

    public void ApplyHdr() => ApplyGroup(MpvOptionsBuilder.Hdr(Settings));
    public void ApplyAudio() => ApplyGroup(MpvOptionsBuilder.Audio(Settings));
    public void ApplyDeinterlace() => ApplyGroup(MpvOptionsBuilder.Deinterlace(Settings));
    public void ApplySubtitles() => ApplyGroup(MpvOptionsBuilder.Subtitles(Settings));

    public void ApplyHardwareDecoding() =>
        _mpv.SetProperty("hwdec", Settings.HardwareDecoding ? "auto-safe" : "no");

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
    public void AdjustSubtitleDelay(double seconds) =>
        _mpv.TryCommand("add", "sub-delay", seconds.ToString(CultureInfo.InvariantCulture));

    public void Dispose() => _mpv.Dispose();
}
