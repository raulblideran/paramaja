using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Paramaja.Core.Mpv;
using Paramaja.Core.Native;
using Paramaja.Core.Settings;

namespace Paramaja.Core.Playback;

/// <summary>
/// Plays DVDs and Blu-rays (discs and VIDEO_TS / BDMV folders) through libVLC, which, unlike
/// mpv, shows their menus. It renders into its own native window and reports state under
/// the same names <see cref="PlayerService"/> uses, so the UI drives both alike. mpv-only
/// settings (HDR tonemapping, render profiles, stream buffering) don't apply to discs.
/// </summary>
public sealed partial class VlcDiscPlayer : IPlayer
{
    private readonly LibVLC _vlc;
    private readonly MediaPlayer _mp;
    private readonly PortablePaths _paths;
    private Media? _media;
    private string? _title;

    public PlayerSettings Settings { get; }
    public PlayerState State { get; } = new();
    public IReadOnlyList<TrackInfo> Tracks { get; private set; } = [];
    public IReadOnlyList<ChapterInfo> Chapters { get; private set; } = [];
    public IReadOnlyList<DiscTitleInfo> Titles { get; private set; } = [];

    public event Action<string>? StateChanged;
    public event Action? TracksChanged;
    public event Action? ChaptersChanged;
#pragma warning disable CS0067 // libVLC reports no keys or mouse to the app: the view polls the pointer itself.
    public event Action<string>? KeyPressed;
    public event Action? MouseMoved;
    public event Action? MouseClicked;
    public event Action<RenderProfile>? RenderProfileChanged;
#pragma warning restore CS0067
    public event Action? FileLoaded;
    public event Action<EndFileReason, string?>? EndFile;

    /// <summary>libVLC can be loaded (it's optional: without it discs play through mpv, menu-less).</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                LibVLCSharp.Shared.Core.Initialize(NativeLibraries.VlcDirectory);
                return true;
            }
            catch (Exception e) when (e is VLCException or DllNotFoundException or InvalidOperationException) { return false; }
        }
    }

    /// <param name="windowId">The native window to render into (HWND on Windows, X11 window on Linux).</param>
    /// <param name="extraOptions">Added to the defaults: tests use "--vout=dummy" and the like.</param>
    public VlcDiscPlayer(PlayerSettings settings, PortablePaths paths, long? windowId, params string[] extraOptions)
    {
        Settings = settings;
        _paths = paths;
        LibVLCSharp.Shared.Core.Initialize(NativeLibraries.VlcDirectory);
        _vlc = new LibVLC([
            "--no-video-title-show",   // the app shows the title itself
            "--sub-source=marq",       // on-screen messages (ShowText)
            "--mouse-hide-timeout=2500", // the pointer hides with the controls (MainViewModel.ControlsHideDelay)
            "--quiet",
            .. extraOptions]);
        _mp = new MediaPlayer(_vlc)
        {
            // Keys go to the app (which turns arrows and Enter into menu navigation);
            // mouse clicks go to libVLC, so disc menu buttons can be clicked.
            EnableKeyInput = false,
            EnableMouseInput = true,
            EnableHardwareDecoding = settings.HardwareDecoding,
        };
        if (windowId is { } wid)
        {
            if (OperatingSystem.IsWindows()) _mp.Hwnd = new IntPtr(wid);
            else _mp.XWindow = (uint)wid;
        }
        Wire();
        SetVolume(settings.Volume);
        _mp.Mute = settings.Muted;
    }

    // ---- libVLC events (on libVLC's thread; calling back into libVLC there can deadlock) ----

    private void Wire()
    {
        _mp.TimeChanged += (_, e) => Set("time-pos", () => State.Position = e.Time / 1000.0);
        _mp.LengthChanged += (_, e) => Set("duration", () => State.Duration = e.Length / 1000.0);
        _mp.Playing += (_, _) =>
        {
            Set("pause", () => State.IsPaused = false);
            Set("idle-active", () => State.IsIdle = false);
            Set("paused-for-cache", () => State.IsBuffering = false);
        };
        _mp.Paused += (_, _) => Set("pause", () => State.IsPaused = true);
        _mp.Buffering += (_, e) => Set("paused-for-cache", () => State.IsBuffering = e.Cache < 100);
        _mp.Stopped += (_, _) => Set("idle-active", () => State.IsIdle = true);
        _mp.EndReached += (_, _) =>
        {
            Set("idle-active", () => State.IsIdle = true);
            EndFile?.Invoke(EndFileReason.Eof, null);
        };
        _mp.EncounteredError += (_, _) =>
        {
            Set("idle-active", () => State.IsIdle = true);
            EndFile?.Invoke(EndFileReason.Error, "the disc couldn't be read (encrypted, damaged or unsupported)");
        };
        _mp.TitleChanged += (_, _) => Later(() =>
        {
            RefreshChapters();
            Set("media-title", () => State.Title = _title);
        });
        _mp.ChapterChanged += (_, e) => Set("chapter", () => State.Chapter = e.Chapter);
        _mp.ESAdded += (_, _) => Later(RefreshTracks);
        _mp.ESDeleted += (_, _) => Later(RefreshTracks);
        _mp.ESSelected += (_, _) => Later(RefreshTracks);
        _mp.Vout += (_, e) => Later(() =>
        {
            uint w = 0, h = 0;
            if (e.Count > 0 && _mp.Size(0, ref w, ref h))
            {
                Set("video-params/w", () => State.VideoWidth = (int)w);
                Set("video-params/h", () => State.VideoHeight = (int)h);
            }
            Set("video-format", () => State.VideoFormat = "disc");
            Set("hwdec-current", () => State.HwdecCurrent = _mp.EnableHardwareDecoding ? "auto" : "no");
        });
        _mp.Muted += (_, _) => Set("mute", () => State.IsMuted = true);
        _mp.Unmuted += (_, _) => Set("mute", () => State.IsMuted = false);
    }

    private void Set(string name, Action update)
    {
        update();
        StateChanged?.Invoke(name);
    }

    /// <summary>Runs off libVLC's event thread, where libVLC may be called again.</summary>
    private static void Later(Action action) => Task.Run(() =>
    {
        try { action(); }
        catch (Exception e) when (e is VLCException or ObjectDisposedException) { }
    });

    // ---- Opening ----------------------------------------------------------------------------

    /// <summary>
    /// A disc: a VIDEO_TS / BDMV folder, a mounted disc, or a raw drive (/dev/sr0, D:\).
    /// DVDs start at their menu (dvdnav), Blu-rays at their first-play menu where they have one.
    /// </summary>
    public void OpenDisc(string root, bool bluray)
    {
        var mrl = (bluray ? "bluray://" : "dvd://") + DiscPath(root);
        _title = Path.GetFileName(root.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : root;
        _media?.Dispose();
        _media = new Media(_vlc, mrl, FromType.FromLocation);
        Chapters = [];
        Titles = [];
        ChaptersChanged?.Invoke();
        Set("media-title", () => State.Title = _title);
        Set("idle-active", () => State.IsIdle = false);
        _mp.Play(_media);
        ApplyDeinterlace();
        Later(() =>
        {
            Thread.Sleep(500); // titles are known once the disc is open
            RefreshChapters();
            FileLoaded?.Invoke();
        });
    }

    /// <summary>libVLC wants "dvd:///D:/" on Windows and "dvd:///dev/sr0" or "dvd:///home/…" elsewhere.</summary>
    internal static string DiscPath(string root) =>
        OperatingSystem.IsWindows() ? "/" + root.Replace('\\', '/') : root;

    /// <summary>Files, streams and links play through mpv; this engine is for discs only.</summary>
    public void Open(string pathOrUrl, string? displayTitle = null, bool streaming = false) =>
        throw new NotSupportedException("VlcDiscPlayer plays discs only; use OpenDisc");

    public void Stop() => Later(() => _mp.Stop());

    // ---- Disc menus ----------------------------------------------------------------------

    public bool HasDiscMenu => FullTitles().Any(t => t.IsMenu);

    public bool IsInDiscMenu
    {
        get
        {
            var titles = FullTitles();
            var current = _mp.Title;
            return current >= 0 && current < titles.Count && (titles[current].IsMenu || titles[current].IsInteractive);
        }
    }

    public void NavigateMenu(MenuNavigation direction) => _mp.Navigate(direction switch
    {
        MenuNavigation.Up => (uint)NavigationMode.Up,
        MenuNavigation.Down => (uint)NavigationMode.Down,
        MenuNavigation.Left => (uint)NavigationMode.Left,
        MenuNavigation.Right => (uint)NavigationMode.Right,
        _ => (uint)NavigationMode.Activate,
    });

    /// <summary>
    /// The root menu: the disc's first menu title (DVD "title 0", a Blu-ray's top menu). A
    /// Blu-ray without one still has its pop-up menu.
    /// </summary>
    public void ShowDiscMenu()
    {
        var menu = FullTitles().FindIndex(t => t.IsMenu);
        if (menu >= 0) _mp.Title = menu;
        else _mp.Navigate((uint)NavigationMode.Popup);
    }

    private sealed record FullTitle(long DurationMs, string? Name, bool IsMenu, bool IsInteractive);

    // libVLC knows which titles are menus; LibVLCSharp 3 doesn't pass that on, so ask it directly.
    [StructLayout(LayoutKind.Sequential)]
    private struct TitleDescription
    {
        public long Duration;
        public IntPtr Name;
        public uint Flags;
    }

    private const uint TitleMenu = 0x01, TitleInteractive = 0x02;

    [LibraryImport(NativeLibraries.Vlc)]
    private static partial int libvlc_media_player_get_full_title_descriptions(IntPtr player, out IntPtr titles);

    [LibraryImport(NativeLibraries.Vlc)]
    private static partial void libvlc_title_descriptions_release(IntPtr titles, uint count);

    static VlcDiscPlayer() => NativeLibraries.Register();

    private List<FullTitle> FullTitles()
    {
        var list = new List<FullTitle>();
        var count = libvlc_media_player_get_full_title_descriptions(_mp.NativeReference, out var array);
        if (count <= 0) return list;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var d = Marshal.PtrToStructure<TitleDescription>(Marshal.ReadIntPtr(array, i * IntPtr.Size));
                list.Add(new FullTitle(d.Duration, Marshal.PtrToStringUTF8(d.Name),
                    (d.Flags & TitleMenu) != 0, (d.Flags & TitleInteractive) != 0));
            }
        }
        finally
        {
            libvlc_title_descriptions_release(array, (uint)count);
        }
        return list;
    }

    // ---- Titles and chapters ----------------------------------------------------------------

    private void RefreshChapters()
    {
        var titles = FullTitles();
        var current = _mp.Title;
        Titles = titles
            .Select((t, i) => (t, i))
            .Where(x => !x.t.IsMenu)
            .Select(x => new DiscTitleInfo(x.i, TitleLabel(x.t, x.i), x.i == current))
            .ToList();
        // A menu's "chapters" are its pages, not something to jump to.
        var inMenu = current >= 0 && current < titles.Count && titles[current].IsMenu;
        Chapters = current < 0 || inMenu ? [] : _mp.FullChapterDescriptions(current)
            .Select((c, i) => new ChapterInfo(i, string.IsNullOrWhiteSpace(c.Name) ? $"Chapter {i + 1}" : c.Name, c.TimeOffset / 1000.0))
            .ToList();
        State.Chapter = _mp.Chapter;
        ChaptersChanged?.Invoke();
    }

    private static string TitleLabel(FullTitle t, int index) =>
        $"{(string.IsNullOrWhiteSpace(t.Name) ? $"Title {index}" : t.Name)} ({TimeFormat.Format(t.DurationMs / 1000.0)})";

    public void GoToChapter(int index) => _mp.Chapter = index;
    public void StepChapter(int delta)
    {
        if (delta > 0) _mp.NextChapter();
        else _mp.PreviousChapter();
    }

    public void SelectTitle(int id) => _mp.Title = id;

    // ---- Tracks -------------------------------------------------------------------------------

    private void RefreshTracks()
    {
        var list = new List<TrackInfo>();
        var audio = _mp.AudioTrack;
        foreach (var t in _mp.AudioTrackDescription.Where(t => t.Id >= 0))
            list.Add(new TrackInfo(t.Id, TrackType.Audio, t.Name, null, null, t.Id == audio, false, false, null));
        var spu = _mp.Spu;
        foreach (var t in _mp.SpuDescription.Where(t => t.Id >= 0))
            list.Add(new TrackInfo(t.Id, TrackType.Subtitle, t.Name, null, null, t.Id == spu, false, false, null));
        Tracks = list;
        TracksChanged?.Invoke();
    }

    public void SelectTrack(TrackType type, int? id)
    {
        if (type == TrackType.Audio && id is { } a) _mp.SetAudioTrack(a);
        else if (type == TrackType.Subtitle) _mp.SetSpu(id ?? -1);
        Later(RefreshTracks);
    }

    public void CycleTrack(TrackType type)
    {
        var ids = (type == TrackType.Audio ? _mp.AudioTrackDescription : _mp.SpuDescription).Select(t => t.Id).ToList();
        if (ids.Count == 0) return;
        var current = type == TrackType.Audio ? _mp.AudioTrack : _mp.Spu;
        var next = ids[(ids.IndexOf(current) + 1) % ids.Count];
        SelectTrack(type, next);
    }

    public void AddSubtitle(string path) =>
        _mp.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true);

    private bool _subtitlesHidden;
    private int _lastSpu = -1;

    public void ToggleSubtitleVisibility()
    {
        if (_subtitlesHidden) _mp.SetSpu(_lastSpu);
        else { _lastSpu = _mp.Spu; _mp.SetSpu(-1); }
        _subtitlesHidden = !_subtitlesHidden;
    }

    public double AdjustSubtitleDelay(double seconds)
    {
        var micro = _mp.SpuDelay + (long)Math.Round(seconds * 1_000_000);
        _mp.SetSpuDelay(micro);
        return Math.Round(micro / 1_000_000.0, 3);
    }

    // ---- Transport ------------------------------------------------------------------------

    public void TogglePause() => _mp.Pause();
    public void SetPaused(bool paused) => _mp.SetPause(paused);
    public void SeekRelative(double seconds) => _mp.Time = Math.Max(0, _mp.Time + (long)(seconds * 1000));
    public void SeekAbsolute(double seconds) => _mp.Time = (long)(Math.Max(0, seconds) * 1000);
    public void FrameStep() => _mp.NextFrame();
    public void FrameBackStep() { } // libVLC can't step backwards

    /// <param name="volume">Slider position 0-150, logarithmic (see <see cref="VolumeCurve"/>).</param>
    public void SetVolume(double volume)
    {
        Settings.Volume = Math.Clamp(volume, 0, VolumeCurve.Max);
        // libVLC's volume is linear in amplitude, like the curve's mpv value is in its scale.
        _mp.Volume = (int)Math.Round(VolumeCurve.ToMpv(Settings.Volume));
        Set("volume", () => State.Volume = Settings.Volume);
    }

    public void ChangeVolume(double delta) => SetVolume(Settings.Volume + delta);

    public void ToggleMute()
    {
        Settings.Muted = !_mp.Mute;
        _mp.Mute = Settings.Muted;
    }

    /// <summary>On-screen text through libVLC's marquee filter.</summary>
    public void ShowText(string text, int durationMs = 1500)
    {
        _mp.SetMarqueeInt(VideoMarqueeOption.Enable, 1);
        _mp.SetMarqueeInt(VideoMarqueeOption.Position, 5); // top left
        _mp.SetMarqueeInt(VideoMarqueeOption.X, 24);
        _mp.SetMarqueeInt(VideoMarqueeOption.Y, 24);
        _mp.SetMarqueeInt(VideoMarqueeOption.Size, 28);
        _mp.SetMarqueeInt(VideoMarqueeOption.Timeout, durationMs);
        _mp.SetMarqueeString(VideoMarqueeOption.Text, text);
    }

    public string Screenshot(bool includeSubtitles)
    {
        Directory.CreateDirectory(_paths.Screenshots);
        var file = Path.Combine(_paths.Screenshots, PlayerService.ScreenshotFileName(State.Title, State.Position, DateTime.Now));
        if (!_mp.TakeSnapshot(0, file, 0, 0)) throw new MpvException("the disc player couldn't take a screenshot");
        return file;
    }

    // ---- Settings that apply to discs ---------------------------------------------------------

    public void SetDeinterlace(DeinterlaceMode mode)
    {
        Settings.Deinterlace = mode;
        ApplyDeinterlace();
    }

    /// <summary>
    /// DVDs are mostly interlaced. libVLC has no BWDIF; yadif2x is its equivalent: motion
    /// adaptive, one frame per field (50i becomes 50p). Auto leaves it to libVLC's own detection.
    /// </summary>
    private void ApplyDeinterlace()
    {
        _mp.SetDeinterlace(Settings.Deinterlace == DeinterlaceMode.Off ? "" : "yadif2x");
        Set("deinterlace-active", () =>
        {
            State.DeinterlaceActive = Settings.Deinterlace != DeinterlaceMode.Off;
            State.DeinterlaceFilter = State.DeinterlaceActive ? "yadif2x" : null;
        });
    }

    public void ApplyHardwareDecoding() => _mp.EnableHardwareDecoding = Settings.HardwareDecoding;

    public void SetDownmix(bool stereo) => Settings.DownmixToStereo = stereo;
    public void ApplyAudio() { }
    public void ApplySubtitles() { }
    public void SetHdrMode(HdrMode mode) => Settings.HdrMode = mode;
    public void SetDisplayHdr(bool isHdr) { }
    public void ApplyHdr() { }
    public void SetCursorHidden(bool hidden) { } // libVLC hides it itself (--mouse-hide-timeout)
    public void ToggleStats() { }
    public HealthSample SampleHealth() => new(DateTime.UtcNow, !State.IsIdle && !State.IsPaused, false, 0, 0, 0, null, null, null, State.Duration);
    public RenderProfile RenderProfile => RenderProfile.Balanced;
    public void FallBackToFastRendering() { }
    public void ApplyRenderQuality() { }
    public double StreamBufferSeconds => 0;
    public void ApplyStreamQuality() { }
    public void ApplyDisplayFps() { }

    public void Dispose()
    {
        _mp.Stop();
        _mp.Dispose();
        _media?.Dispose();
        _vlc.Dispose();
    }
}
