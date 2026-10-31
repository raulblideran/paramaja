using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LibVLCSharp.Shared;
using Paramaja.Core.Mpv;
using Paramaja.Core.Native;
using Paramaja.Core.Settings;

namespace Paramaja.Core.Playback;

/// <summary>
/// Plays discs through libVLC: DVDs and Blu-rays (discs and VIDEO_TS / BDMV folders) with their
/// menus, which mpv can't show, and Audio CDs. It renders into its own native window and reports
/// state under the same names <see cref="PlayerService"/> uses, so the UI drives both alike.
/// mpv-only settings (HDR tonemapping, render profiles, stream buffering) don't apply to discs.
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

    // ---- Startup: libVLC's plugin cache ---------------------------------------------------------

    private static readonly Lazy<Task> Prepared = new(() => Task.Run(Prepare));

    /// <summary>
    /// Readies libVLC in the background; call it at startup. libVLC reads its ~250 plugins from a
    /// cache (plugins.dat), or without one opens every plugin to read it, which on Windows took
    /// seconds and froze the window the first time a disc opened. The Flatpak builds the cache
    /// when it's built; the Windows zip can't ship one (the cache records each plugin's file time,
    /// which unzipping changes), so it's built here, on the user's machine, once.
    /// </summary>
    public static Task PrepareAsync() => Prepared.Value;

    private static void Prepare()
    {
        try
        {
            LibVLCSharp.Shared.Core.Initialize(NativeLibraries.VlcDirectory);
            // A system or Flatpak libVLC keeps its own cache.
            if (NativeLibraries.VlcDirectory is not { } dir || !Directory.Exists(Path.Combine(dir, "plugins"))) return;
            var plugins = Path.Combine(dir, "plugins");
            var stampFile = Path.Combine(plugins, PluginCacheStampFile);
            var stamp = PluginStamp(plugins);
            if (File.Exists(Path.Combine(plugins, "plugins.dat")) && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp)
                return;
            // Writes plugins.dat next to the plugins (where the app folder is writable).
            using (new LibVLC("--reset-plugins-cache", "--quiet")) { }
            if (File.Exists(Path.Combine(plugins, "plugins.dat"))) File.WriteAllText(stampFile, stamp);
        }
        catch (Exception e) when (e is VLCException or DllNotFoundException or InvalidOperationException
                                      or IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Records which plugin files the cache was built from; a different set (a new version unzipped over) rebuilds it.</summary>
    internal const string PluginCacheStampFile = "plugins.dat.paramaja";

    /// <summary>The plugin files' names, sizes and times, which is what libVLC checks its cache against.</summary>
    internal static string PluginStamp(string plugins)
    {
        var lines = Directory.EnumerateFiles(plugins, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".dll" or ".so" or ".dylib")
            .Select(f => new FileInfo(f))
            .Select(f => $"{Path.GetRelativePath(plugins, f.FullName)}|{f.Length}|{new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds()}")
            .Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    // ---- Construction ---------------------------------------------------------------------------

    /// <param name="windowId">The native window to render into (HWND on Windows, X11 window on Linux).</param>
    /// <param name="extraOptions">Added to the defaults: tests use "--vout=dummy" and the like.</param>
    public VlcDiscPlayer(PlayerSettings settings, PortablePaths paths, long? windowId, params string[] extraOptions)
    {
        Settings = settings;
        _paths = paths;
        PrepareAsync().Wait(); // the plugin cache, built once
        _vlc = new LibVLC([
            "--no-video-title-show",   // the app shows the title itself
            "--sub-source=marq",       // on-screen messages (ShowText)
            "--mouse-hide-timeout=3000", // over its video the pointer hides after 3 s (MainViewModel.CursorHideDelay)
            // Paramaja's libVLC runs BWDIF for this mode (see DeinterlaceModeName below); -1
            // deinterlaces only what the disc flags as interlaced.
            "--deinterlace-mode=" + DeinterlaceModeName,
            "--deinterlace=-1",
            "--quiet",
            .. extraOptions]);
        // libVLC tells whether the video is interlaced only in its log (OnLog).
        _vlc.Log += OnLog;
        HasBwdif = _vlc.VideoFilters.Any(f => f.Name == "deinterlace" && f.LongName?.Contains("BWDIF", StringComparison.Ordinal) == true);
        _mp = new MediaPlayer(_vlc)
        {
            // Keys go to the app (which turns arrows and Enter into menu navigation);
            // mouse clicks go to libVLC, so disc menu buttons can be clicked.
            EnableKeyInput = false,
            EnableMouseInput = true,
            // Decided per disc (OpenDisc): DVDs decode in software, Blu-rays on the GPU.
            EnableHardwareDecoding = false,
        };
        if (windowId is { } wid)
        {
            if (OperatingSystem.IsWindows()) _mp.Hwnd = new IntPtr(wid);
            else _mp.XWindow = (uint)wid;
        }
        Wire();
        SetVolume(settings.Volume);
        _mp.Mute = settings.Muted;
        PublishDeinterlace();
    }

    // ---- libVLC events (on libVLC's thread; calling back into libVLC there can deadlock) ----

    private void Wire()
    {
        _mp.TimeChanged += (_, e) => Set("time-pos", () => State.Position = e.Time / 1000.0);
        _mp.LengthChanged += (_, e) => Set("duration", () => State.Duration = e.Length / 1000.0);
        _mp.Playing += (_, _) =>
        {
            _switchingTrack = false;
            Set("pause", () => State.IsPaused = false);
            Set("idle-active", () => State.IsIdle = false);
            Set("paused-for-cache", () => State.IsBuffering = false);
        };
        _mp.Paused += (_, _) => Set("pause", () => State.IsPaused = true);
        _mp.Buffering += (_, e) => Set("paused-for-cache", () => State.IsBuffering = e.Cache < 100);
        _mp.Stopped += (_, _) =>
        {
            if (_listingCd) { _cdListed.Set(); return; }
            // Between two tracks of a CD: not the end of playback.
            if (!_switchingTrack) Set("idle-active", () => State.IsIdle = true);
        };
        _mp.EndReached += (_, _) =>
        {
            if (_listingCd) { _cdListed.Set(); return; }
            // A CD plays on into its next track.
            if (_cdTrack >= 0 && _cdTrack + 1 < _cdTracks.Count)
            {
                var next = _cdTrack + 1;
                _switchingTrack = true;
                Later(() => PlayTrack(next));
                return;
            }
            Set("idle-active", () => State.IsIdle = true);
            EndFile?.Invoke(EndFileReason.Eof, null);
        };
        _mp.EncounteredError += (_, _) =>
        {
            if (_listingCd) { _cdListed.Set(); return; }
            Set("idle-active", () => State.IsIdle = true);
            EndFile?.Invoke(EndFileReason.Error, "the disc couldn't be read (encrypted, damaged or unsupported)");
        };
        _mp.TitleChanged += (_, _) => Later(() =>
        {
            if (_cdTrack >= 0) return; // a CD's tracks are its chapters
            PickStereoSoon();
            ApplyStereoChannel(); // a title's audio output may start out in plain stereo
            RefreshChapters();
            Set("media-title", () => State.Title = _title);
        });
        _mp.ChapterChanged += (_, e) =>
        {
            if (_cdTrack >= 0) return;
            // A title's chapters can become known after it started (no event says so).
            if (e.Chapter >= Chapters.Count) Later(RefreshChapters);
            Set("chapter", () => State.Chapter = e.Chapter);
        };
        _mp.ESAdded += (_, _) => Later(RefreshTracksTwice);
        _mp.ESDeleted += (_, _) => Later(RefreshTracks);
        _mp.ESSelected += (_, _) => Later(RefreshTracks);
        _mp.Vout += (_, e) => Later(() =>
        {
            uint w = 0, h = 0;
            if (e.Count > 0 && _mp.Size(0, ref w, ref h))
            {
                Set("video-params/w", () => State.VideoWidth = (int)w);
                Set("video-params/h", () => State.VideoHeight = (int)h);
                Set("video-params/aspect", () => State.DisplayAspect = DisplayAspect(w, h));
            }
            Set("video-format", () => State.VideoFormat = VideoCodec());
            Set("hwdec-current", () => State.HwdecCurrent = _mp.EnableHardwareDecoding ? "auto" : "no");
            // Entering or leaving a menu starts a new picture: its chapters (none in a menu) go with it.
            if (_cdTrack < 0) RefreshChapters();
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
        ForgetAudioCd();
        StartFresh();
        PickStereoSoon();
        Chapters = [];
        Titles = [];
        ChaptersChanged?.Invoke();
        // DVDs are standard definition MPEG-2, which the CPU decodes with ease (and software frames
        // go straight to the deinterlacer). Blu-rays are HD: the GPU decodes them.
        UseHardwareDecoding(applies: bluray);
        Set("has-video", () => State.HasVideo = true);
        // A folder, a drive, or an image (ISO) of the disc: libVLC opens them all the same way.
        _title = PlayerService.DiscTitle(root);
        _titleAspects = bluray ? new Dictionary<int, double>() : Input.DvdAspect.TitleAspects(root);
        _titleAudio = bluray ? new Dictionary<int, IReadOnlyList<Ripping.Dvd.DvdAudio>>() : TitleAudio(root);
        Play(new Media(_vlc, (bluray ? "bluray://" : "dvd://") + DiscPath(root), FromType.FromLocation));
        Later(() =>
        {
            Thread.Sleep(500); // titles are known once the disc is open
            RefreshChapters();
            FileLoaded?.Invoke();
        });
    }

    private void Play(Media media)
    {
        var old = _media;
        _media = media;
        Set("media-title", () => State.Title = _title);
        Set("idle-active", () => State.IsIdle = false);
        _switchingTrack = true; // what played before stops: not the end of playback
        _mp.Play(media);
        old?.Dispose();
    }

    /// <summary>libVLC wants "dvd:///D:/" on Windows and "dvd:///dev/sr0" or "dvd:///home/…" elsewhere.</summary>
    internal static string DiscPath(string root) =>
        OperatingSystem.IsWindows() ? "/" + root.Replace('\\', '/') : root;

    /// <summary>Files, streams and links play through mpv; this engine is for discs only.</summary>
    public void Open(string pathOrUrl, string? displayTitle = null, bool streaming = false) =>
        throw new NotSupportedException("VlcDiscPlayer plays discs only; use OpenDisc");

    /// <summary>SACD images play through mpv (Sacd.SacdProtocol).</summary>
    public void OpenSacd(string image) => throw new NotSupportedException("SACD images play through mpv");

    public void Stop() => Later(() =>
    {
        ForgetAudioCd();
        _mp.Stop();
        // Nothing plays: no chapters or titles to show the next time the bars come up.
        Chapters = [];
        Titles = [];
        ChaptersChanged?.Invoke();
    });

    // ---- Audio CDs ----------------------------------------------------------------------------

    /// <summary>The CD's tracks (libVLC lists them as items under the disc); empty unless a CD plays.</summary>
    private List<Media> _cdTracks = [];
    private Media? _cd;
    private int _cdTrack = -1;
    /// <summary>A new track is starting: the old one's "stopped" isn't the end of playback.</summary>
    private volatile bool _switchingTrack;
    /// <summary>The disc itself is "playing", which only lists its tracks; its end isn't the end of playback.</summary>
    private volatile bool _listingCd;
    private readonly ManualResetEventSlim _cdListed = new();

    /// <summary>
    /// An Audio CD from its drive (/dev/sr0, D:\), or a CD image's cue sheet (BIN/CUE), which
    /// libVLC's CD reader takes in place of a drive. Its tracks are the chapters: Page Up / Down
    /// and the chapters menu move between them, and playback carries on from one to the next.
    /// </summary>
    public void OpenAudioCd(string device)
    {
        ForgetAudioCd();
        StartFresh();
        _title = File.Exists(device) && !Input.InputClassifier.IsOpticalDevice(device) ? Path.GetFileNameWithoutExtension(device) : "Audio CD";
        Set("has-video", () => State.HasVideo = false);
        Titles = [];
        Chapters = [];
        ChaptersChanged?.Invoke();
        UseHardwareDecoding(applies: false);
        Set("media-title", () => State.Title = _title);
        Set("idle-active", () => State.IsIdle = false);
        Later(() => LoadAudioCd(device));
    }

    /// <summary>
    /// libVLC lists a CD's tracks (from its table of contents, with CD-Text titles where the disc
    /// has them) only by playing the disc itself, which ends straight away; its preparser skips
    /// discs. Then the first track plays.
    /// </summary>
    private void LoadAudioCd(string device)
    {
        var cd = _cd = new Media(_vlc, "cdda://" + DiscPath(device), FromType.FromLocation);
        _cdListed.Reset();
        _listingCd = true;
        _mp.Play(cd);
        _cdListed.Wait(TimeSpan.FromSeconds(30));
        // The listing's own "stopped" may still come: it isn't the end of playback either.
        _switchingTrack = true;
        _listingCd = false;
        if (!ReferenceEquals(cd, _cd)) return; // something else was opened meanwhile
        var tracks = cd.SubItems.ToList();
        if (tracks.Count == 0)
        {
            _switchingTrack = false;
            Set("idle-active", () => State.IsIdle = true);
            EndFile?.Invoke(EndFileReason.Error, "no audio tracks found on the disc (is it an Audio CD?)");
            return;
        }
        if (tracks[0].Meta(MetadataType.Album) is { Length: > 0 } album) _title = album;
        // A CD image's cue sheet names the album and its tracks, which libVLC leaves out.
        var sheet = CueSheetOf(device);
        if (sheet?.Title is { Length: > 0 } cueAlbum) _title = cueAlbum;
        _cdTracks = tracks;
        // Each track is its own item, so there are no times into the disc to show.
        Chapters = tracks.Select((t, i) => new ChapterInfo(i,
            sheet is not null && i < sheet.Tracks.Count && !string.IsNullOrWhiteSpace(sheet.Tracks[i].Title) ? sheet.Tracks[i].Title! : TrackTitle(t, i),
            double.NaN)).ToList();
        ChaptersChanged?.Invoke();
        PlayTrack(0);
        FileLoaded?.Invoke();
    }

    private static CueSheet.Sheet? CueSheetOf(string device)
    {
        if (!device.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)) return null;
        try { return CueSheet.Read(device); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>CD-Text's title where the disc has one, else "Track 3" (libVLC's own is "Audio CD - Track 03").</summary>
    private static string TrackTitle(Media track, int index) =>
        track.Meta(MetadataType.Title) is { Length: > 0 } t && !t.StartsWith("Audio CD - Track", StringComparison.Ordinal)
            && !t.StartsWith("cdda:", StringComparison.OrdinalIgnoreCase)
            ? t
            : $"Track {index + 1}";

    private void PlayTrack(int index)
    {
        if (index < 0 || index >= _cdTracks.Count) return;
        _switchingTrack = true;
        _cdTrack = index;
        _mp.Play(_cdTracks[index]);
        Set("chapter", () => State.Chapter = index);
        Set("media-title", () => State.Title = $"{_title} — {Chapters[index].Title}");
    }

    private void ForgetAudioCd()
    {
        _cdTrack = -1;
        _switchingTrack = false;
        _listingCd = false;
        _cdListed.Set();
        if (_cdTracks.Count == 0 && _cd is null) return;
        foreach (var t in _cdTracks) t.Dispose();
        _cdTracks = [];
        _cd?.Dispose();
        _cd = null;
    }

    // ---- Disc menus ----------------------------------------------------------------------

    public bool HasDiscMenu => _cdTrack < 0 && FullTitles().Any(t => t.IsMenu);

    public bool IsInDiscMenu
    {
        get
        {
            if (_cdTrack >= 0) return false;
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
        if (_cdTrack >= 0) return;
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

    public void GoToChapter(int index)
    {
        if (_cdTrack >= 0) Later(() => PlayTrack(index));
        else _mp.Chapter = index;
    }

    /// <summary>Next (+1) or previous (-1) chapter, or CD track; a few seconds into a track, back goes to its start.</summary>
    public void StepChapter(int delta)
    {
        if (_cdTrack >= 0)
        {
            var target = delta < 0 && State.Position > 3 ? _cdTrack : _cdTrack + Math.Sign(delta);
            Later(() => PlayTrack(Math.Clamp(target, 0, _cdTracks.Count - 1)));
        }
        else if (delta > 0) _mp.NextChapter();
        else _mp.PreviousChapter();
    }

    public void SelectTitle(int id) => _mp.Title = id;

    /// <summary>Each disc (or CD) starts afresh: its own video, its own audio, every switch on its default.</summary>
    private void StartFresh()
    {
        _pickStereo = false;
        _lastDisplayed = null;
        ResetDeinterlacing();
        SetStereoChannel(StereoChannel.Both);
        SetPictureFit(PictureFit.Original);
        Set("video-params/aspect", () => State.DisplayAspect = null);
        Set("video-format", () => State.VideoFormat = null);
        Set("audio-params/channel-count", () => State.SourceChannels = null);
    }

    // ---- Tracks -------------------------------------------------------------------------------

    private void RefreshTracks()
    {
        var audio = _mp.AudioTrack;
        var audioIds = _mp.AudioTrackDescription.Where(t => t.Id >= 0).Select(t => t.Id).ToList();
        var channels = MatchChannels(MediaTracks(), audioIds);
        // libVLC tells a DVD's LPCM channels at once but an AC-3 or DTS track's only once decoded
        // (not at all in its track list): the disc's IFO says them.
        if (_cdTrack < 0 && _titleAudio.TryGetValue(_mp.Title, out var ifo))
            foreach (var id in audioIds)
                if (!channels.ContainsKey(id) && DvdAudioIndex(id) is { } n && n < ifo.Count) channels[id] = ifo[n].Channels;
        var list = new List<TrackInfo>();
        foreach (var t in _mp.AudioTrackDescription.Where(t => t.Id >= 0))
            list.Add(new TrackInfo(t.Id, TrackType.Audio, t.Name, null, null, t.Id == audio, false, false,
                channels.TryGetValue(t.Id, out var ch) ? ch : null));
        var spu = _mp.Spu;
        foreach (var t in _mp.SpuDescription.Where(t => t.Id >= 0))
            list.Add(new TrackInfo(t.Id, TrackType.Subtitle, t.Name, null, null, t.Id == spu, false, false, null));
        Tracks = list;
        TracksChanged?.Invoke();
        int? playing = channels.TryGetValue(audio, out var c) ? c : null;
        if (playing != State.SourceChannels)
        {
            Set("audio-params/channel-count", () => State.SourceChannels = playing);
            ApplyStereoChannel(); // a 5.1 track plays as 5.1, a stereo one as stereo
        }
        PickStereo(list, audio);
    }

    /// <summary>libVLC fills in a new track's channel count a moment after announcing it: look again then.</summary>
    private void RefreshTracksTwice()
    {
        RefreshTracks();
        Thread.Sleep(1500);
        RefreshTracks();
    }

    /// <summary>
    /// Audio track id → channels. libVLC 3 lists a disc's tracks (every title adds its own, under
    /// ids of their own) apart from the ids it plays them by; the playing title's audio tracks
    /// are the ones after its video track, in the order the track list gives the ids. Where the
    /// two lists don't line up, nothing is matched.
    /// </summary>
    internal static Dictionary<int, int> MatchChannels(IReadOnlyList<(bool IsVideo, bool IsAudio, int Channels)> mediaTracks,
        IReadOnlyList<int> audioIds)
    {
        var lastVideo = -1;
        for (var i = 0; i < mediaTracks.Count; i++) if (mediaTracks[i].IsVideo) lastVideo = i;
        var audio = mediaTracks.Skip(lastVideo + 1).Where(t => t.IsAudio).Select(t => t.Channels).ToList();
        var map = new Dictionary<int, int>();
        if (audio.Count != audioIds.Count) return map;
        for (var i = 0; i < audio.Count; i++) if (audio[i] > 0) map[audioIds[i]] = audio[i];
        return map;
    }

    private List<(bool IsVideo, bool IsAudio, int Channels)> MediaTracks()
    {
        using var media = _mp.Media;
        if (media is null) return [];
        return media.Tracks.Select(t => (t.TrackType == LibVLCSharp.Shared.TrackType.Video, t.TrackType == LibVLCSharp.Shared.TrackType.Audio,
            t.TrackType == LibVLCSharp.Shared.TrackType.Audio ? (int)t.Data.Audio.Channels : 0)).ToList();
    }

    /// <summary>FFmpeg's name for the playing video's codec ("mpeg2video" on a DVD).</summary>
    private string? VideoCodec()
    {
        using var media = _mp.Media;
        if (media is null) return null;
        var tracks = media.Tracks;
        var video = Array.FindLastIndex(tracks, t => t.TrackType == LibVLCSharp.Shared.TrackType.Video);
        return video < 0 ? null : CodecLabels.FromFourCC(FourCC(tracks[video].Codec));
    }

    private static string FourCC(uint c) =>
        new([(char)(c & 0xff), (char)((c >> 8) & 0xff), (char)((c >> 16) & 0xff), (char)(c >> 24)]);

    // ---- A stereo track by default ----------------------------------------------------------

    /// <summary>A disc's titles bring their tracks a moment after they start; a stereo one is picked meanwhile.</summary>
    private static readonly TimeSpan StereoPickWindow = TimeSpan.FromSeconds(5);
    private volatile bool _pickStereo;
    private DateTime _pickUntil;

    private void PickStereoSoon()
    {
        _pickUntil = DateTime.UtcNow + StereoPickWindow;
        _pickStereo = true;
    }

    /// <summary>
    /// Concert discs often carry the stereo mix beside 5.1 and DTS: where the playing title has
    /// a stereo track, it plays, unless a track has been picked by hand.
    /// </summary>
    private void PickStereo(IReadOnlyList<TrackInfo> tracks, int playing)
    {
        if (!_pickStereo) return;
        if (DateTime.UtcNow > _pickUntil) { _pickStereo = false; return; }
        if (MpvOptionsBuilder.StereoTrack(tracks, Settings.PreferredAudioLanguages) is { } id && id != playing)
            _mp.SetAudioTrack(id);
    }

    public void SelectTrack(TrackType type, int? id)
    {
        if (type == TrackType.Audio) _pickStereo = false; // picked by hand
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
        // The marquee is placed in the video's own pixels.
        _mp.SetMarqueeInt(VideoMarqueeOption.Y, 24 + (int)Math.Round(_osdInset * (State.VideoHeight ?? 576)));
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

    // ---- Deinterlacing -----------------------------------------------------------------------

    /// <summary>
    /// libVLC's mode names are fixed (its API and core accept only their own list), and none is
    /// BWDIF. Paramaja's libVLC builds patch the deinterlacer so this mode runs BWDIF instead
    /// (packaging/vlc/vlc-bwdif.patch); a stock libVLC runs its own yadif2x, the closest one.
    /// </summary>
    private const string DeinterlaceModeName = "yadif2x";

    /// <summary>This libVLC's deinterlacer is Paramaja's, with BWDIF (its description says so).</summary>
    public bool HasBwdif { get; }

    /// <summary>libVLC's video output found the pictures flagged interlaced (and so deinterlaces them).</summary>
    private volatile bool _interlaced;
    /// <summary>Switched off for this disc; every disc starts with it on.</summary>
    private volatile bool _deinterlaceOff;

    /// <summary>
    /// libVLC's video output decides from each picture's flags (-1, "automatic") and says so only
    /// in its log: "Detected interlaced video", and with every change of the setting
    /// "deinterlace -1, mode yadif2x, is_needed 1". A new output (a new title) starts at 0.
    /// Called on libVLC's threads: no calls back into libVLC here.
    /// </summary>
    private void OnLog(object? sender, LogEventArgs e)
    {
        if (InterlacedFromLog(e.Message) is { } interlaced) SetInterlaced(interlaced);
        if (e.Level >= LogLevel.Warning) _messages.Add($"[{e.Module}] {e.Level}: {e.Message}");
    }

    private static readonly Regex IsNeeded = new(@"^deinterlace -?\d+, mode \S*, is_needed (\d)", RegexOptions.CultureInvariant);

    internal static bool? InterlacedFromLog(string? message) => message switch
    {
        null => null,
        _ when message.StartsWith("Detected interlaced video", StringComparison.Ordinal) => true,
        _ when message.StartsWith("Detected progressive video", StringComparison.Ordinal) => false,
        _ => IsNeeded.Match(message) is { Success: true } m ? m.Groups[1].Value != "0" : null,
    };

    private void SetInterlaced(bool interlaced)
    {
        if (_interlaced == interlaced) return;
        _interlaced = interlaced;
        if (interlaced) _messages.Add("libVLC: the video is interlaced (flagged)");
        PublishDeinterlace();
    }

    /// <summary>The video menu's switch, there only while the disc's video is interlaced.</summary>
    public void SetDeinterlacing(bool on)
    {
        if (!_interlaced || _deinterlaceOff == !on) return;
        _deinterlaceOff = !on;
        if (on)
        {
            _mp.SetDeinterlace(DeinterlaceModeName);
            // libVLC's API has no "automatic": set the player's own variable back, which the next
            // video output (the next title) takes.
            SetPlayerInteger("deinterlace", -1);
        }
        else _mp.SetDeinterlace(""); // this title and the next ones, until the disc changes
        PublishDeinterlace();
    }

    private void ResetDeinterlacing()
    {
        _interlaced = false;
        if (_deinterlaceOff)
        {
            _deinterlaceOff = false;
            SetPlayerInteger("deinterlace", -1);
        }
        PublishDeinterlace();
    }

    private void PublishDeinterlace()
    {
        var active = _interlaced && !_deinterlaceOff;
        Set("deinterlace-active", () =>
        {
            State.SourceInterlaced = _interlaced;
            State.InterlaceDetectedBy = _interlaced ? PlayerState.ByFlags : null;
            State.DeinterlacingOn = !_deinterlaceOff;
            State.DeinterlaceActive = active;
            State.DeinterlaceFilter = active ? HasBwdif ? "BWDIF" : "yadif2x" : null;
        });
    }

    [LibraryImport(NativeLibraries.VlcCore, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int var_SetChecked(IntPtr obj, string name, int type, long value);

    private const int VlcVarInteger = 0x0030;

    /// <summary>Sets one of the media player's own variables (a libvlc_media_player_t is a VLC object).</summary>
    private void SetPlayerInteger(string name, long value)
    {
        try { var_SetChecked(_mp.NativeReference, name, VlcVarInteger, value); }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { }
    }

    // ---- Hardware decoding ---------------------------------------------------------------------

    private void UseHardwareDecoding(bool applies)
    {
        _mp.EnableHardwareDecoding = applies;
        Set("hardware-decoding", () =>
        {
            State.HardwareDecodingApplies = applies;
            State.HardwareDecodingOn = applies;
        });
    }

    /// <summary>For the playing Blu-ray: the video decoder restarts (its track is reselected) to switch.</summary>
    public void SetHardwareDecoding(bool on)
    {
        if (!State.HardwareDecodingApplies || State.HardwareDecodingOn == on) return;
        _mp.EnableHardwareDecoding = on;
        Set("hardware-decoding", () => State.HardwareDecodingOn = on);
        Later(() =>
        {
            var track = _mp.VideoTrack;
            if (track < 0) return;
            _mp.SetVideoTrack(-1);
            _mp.SetVideoTrack(track);
        });
    }

    // ---- Audio: one channel of a stereo source ------------------------------------------------

    public void SetStereoChannel(StereoChannel channel)
    {
        Set("stereo-channel", () => State.StereoChannel = channel);
        ApplyStereoChannel();
    }

    /// <summary>
    /// libVLC's "stereo mode". Its Stereo setting isn't neutral: a 5.1 or 7.1 track played under
    /// it is downmixed to two channels, which is what the downmix switch asks for. Otherwise, where
    /// the track has more than two (or isn't known yet), the mode is libVLC's "Original" (unset),
    /// which leaves the channels as they are.
    /// </summary>
    private void ApplyStereoChannel() =>
        _mp.SetChannel(StereoMode(State.StereoChannel, State.SourceChannels, Settings.DownmixToStereo && !Settings.AudioPassthrough));

    /// <summary>libVLC's stereo-mode values (libvlc_audio_output_channel_t, plus 0, "unset").</summary>
    internal static AudioOutputChannel StereoMode(StereoChannel channel, int? sourceChannels, bool downmix = false) => channel switch
    {
        StereoChannel.Left => AudioOutputChannel.Left,
        StereoChannel.Right => AudioOutputChannel.Right,
        _ when sourceChannels == 2 => AudioOutputChannel.Stereo,
        // Downmixing, libVLC's Stereo leaves stereo as it is: a track not known yet gets it too.
        _ when downmix && sourceChannels is null or > 2 => AudioOutputChannel.Stereo,
        _ => OriginalChannels,
    };

    /// <summary>AOUT_VAR_CHAN_UNSET: libVLC offers it as "Original" for sources with other than two channels.</summary>
    internal const AudioOutputChannel OriginalChannels = 0;

    // ---- The picture's shape -------------------------------------------------------------------

    /// <summary>The DVD's titles' shapes (4:3, 16:9), from its IFOs; libVLC's title n is the DVD's title n.</summary>
    private IReadOnlyDictionary<int, double> _titleAspects = new Dictionary<int, double>();

    /// <summary>Each DVD title's audio streams as its IFO lists them (codec, channels), by title number.</summary>
    private IReadOnlyDictionary<int, IReadOnlyList<Ripping.Dvd.DvdAudio>> _titleAudio = new Dictionary<int, IReadOnlyList<Ripping.Dvd.DvdAudio>>();

    private static IReadOnlyDictionary<int, IReadOnlyList<Ripping.Dvd.DvdAudio>> TitleAudio(string root)
    {
        try { return Ripping.Dvd.DvdInfo.Read(root).Titles.ToDictionary(t => t.Number, t => t.Audio); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return new Dictionary<int, IReadOnlyList<Ripping.Dvd.DvdAudio>>();
        }
    }

    /// <summary>
    /// The IFO audio stream a libVLC DVD track is: its id is the MPEG stream's (0xBD00 and the
    /// private stream's sub-id for AC-3 0x80+n, DTS 0x88+n, LPCM 0xA0+n; 0xC0+n for MPEG audio),
    /// whose low three bits are the stream's number n.
    /// </summary>
    internal static int? DvdAudioIndex(int id) => id switch
    {
        >= 0xBD80 and <= 0xBDAF => id & 0x07,
        >= 0xC0 and <= 0xC7 => id & 0x07,
        _ => null,
    };

    /// <summary>
    /// The picture's displayed width over height: a DVD title's as its IFO says (libVLC leaves a
    /// DVD's pixel shape out), else from its size and the video track's pixel shape.
    /// </summary>
    private double? DisplayAspect(uint width, uint height)
    {
        if (width == 0 || height == 0) return null;
        if (_cdTrack < 0 && _titleAspects.TryGetValue(_mp.Title, out var dvd)) return dvd;
        using var media = _mp.Media;
        var video = media?.Tracks.LastOrDefault(t => t.TrackType == LibVLCSharp.Shared.TrackType.Video);
        var (num, den) = video is { } v && v.Data.Video.SarNum > 0 && v.Data.Video.SarDen > 0
            ? (v.Data.Video.SarNum, v.Data.Video.SarDen)
            : (1u, 1u);
        return width * (double)num / (height * (double)den);
    }

    public void SetPictureFit(PictureFit fit)
    {
        _mp.AspectRatio = fit == PictureFit.Resize4x3 ? "4:3" : null;
        _mp.CropGeometry = fit == PictureFit.Crop16x9 ? "16:9" : null;
        Set("picture-fit", () => State.PictureFit = fit);
    }

    // ---- On-screen messages -------------------------------------------------------------------

    /// <summary>The share of the video's height the top bar covers; messages start below it.</summary>
    private double _osdInset;

    public void SetOsdTopInset(double fraction) => _osdInset = Math.Clamp(fraction, 0, 1);

    // ---- Statistics and the log ----------------------------------------------------------------

    private readonly RecentLines _messages = new();
    public IReadOnlyList<string> RecentMessages() => _messages.ToList();

    private (DateTime At, int Displayed)? _lastDisplayed;

    public HealthSample SampleHealth()
    {
        var stats = Statistics();
        double? fps = null;
        if (stats is { } st)
        {
            var now = DateTime.UtcNow;
            if (_lastDisplayed is { } last && now > last.At && st.DisplayedPictures >= last.Displayed)
                fps = (st.DisplayedPictures - last.Displayed) / (now - last.At).TotalSeconds;
            _lastDisplayed = (now, st.DisplayedPictures);
        }
        return new HealthSample(DateTime.UtcNow, !State.IsIdle && !State.IsPaused, State.IsBuffering,
            stats?.LostPictures, null, null, stats is { } s ? s.DemuxBitrate * 1_000_000 : null, null, null, State.Duration)
        {
            Position = State.Position,
            OutputFps = fps,
        };
    }

    private MediaStats? Statistics()
    {
        try
        {
            using var media = _mp.Media;
            return media?.Statistics;
        }
        catch (VLCException) { return null; }
    }

    public IReadOnlyList<KeyValuePair<string, string>> Details()
    {
        var stats = Statistics();
        return
        [
            new("Versions", $"libVLC {_vlc.Version}"),
            new("Deinterlacer", HasBwdif ? "Paramaja's BWDIF" : "libVLC's yadif2x (this libVLC has no BWDIF)"),
            new("Interlaced", _interlaced ? $"yes (flagged), deinterlacing {(_deinterlaceOff ? "switched off" : "on")}" : "no"),
            new("Hardware decoding", _mp.EnableHardwareDecoding ? "on" : "off"),
            new("Video codec", State.VideoFormat ?? "n/a"),
            new("Pictures", stats is { } s ? $"decoded {s.DecodedVideo}, displayed {s.DisplayedPictures}, lost {s.LostPictures}" : "n/a"),
            new("Audio", $"{State.SourceChannels?.ToString() ?? "?"}ch, channel {State.StereoChannel}"),
        ];
    }

    public bool? QueryPaused() => _mp.State switch
    {
        VLCState.Paused => true,
        VLCState.Playing => false,
        _ => null,
    };

    // ---- Settings that don't apply to discs (or that apply as above) -----------------------------

    public void SetDownmix(bool stereo)
    {
        Settings.DownmixToStereo = stereo;
        ApplyAudio();
    }

    /// <summary>The downmix switch (passthrough has none on discs: libVLC decodes everything).</summary>
    public void ApplyAudio() => Later(ApplyStereoChannel);
    public void ApplySubtitles() { }
    public void SetHdrMode(HdrMode mode) => Settings.HdrMode = mode;
    public void SetDisplayHdr(bool isHdr) { }
    public void ApplyHdr() { }
    public void SetCursorHidden(bool hidden) { } // libVLC hides it itself (--mouse-hide-timeout)
    public void ToggleStats() { }
    public RenderProfile RenderProfile => RenderProfile.Balanced;
    public void FallBackToFastRendering() { }
    public void ApplyRenderQuality() { }
    public double StreamBufferSeconds => 0;
    public void ApplyStreamQuality() { }
    public void ReloadStream(int maxHeight) { }
    public void ApplyTiming() { }

    public void Dispose()
    {
        _mp.Stop();
        ForgetAudioCd();
        _mp.Dispose();
        _media?.Dispose();
        _vlc.Dispose();
    }
}
