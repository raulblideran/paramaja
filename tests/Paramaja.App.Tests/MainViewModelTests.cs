using Paramaja.App.Services;
using Paramaja.Core.Input;
using Paramaja.App.ViewModels;
using Paramaja.Core.Mpv;
using Paramaja.Core.Playback;
using Paramaja.Core.Settings;
using Paramaja.Core.Torrent;

namespace Paramaja.App.Tests;

public sealed class Rig : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "paramaja-app-tests", Guid.NewGuid().ToString("N"));
    public PortablePaths Paths { get; }
    public SettingsStore Store { get; }
    public PlayerSettings Settings { get; }
    public FakePlayer Player { get; }
    /// <summary>The disc engine (libVLC in the app), made when the first disc opens.</summary>
    public FakePlayer? DiscPlayer { get; private set; }
    public FakeTorrents Torrents { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public MainViewModel Vm { get; }
    public long? AttachedWid { get; private set; }

    public Rig(PlayerSettings? settings = null, bool attach = true, YtDlp? ytdlp = null, bool discEngine = false)
    {
        Directory.CreateDirectory(Root);
        // Downloads go to the test folder, never the real one.
        Paths = new PortablePaths(Root)
        {
            Downloads = Path.Combine(Root, "Downloads"), Music = Path.Combine(Root, "Music"), Videos = Path.Combine(Root, "Videos"),
        };
        Store = new SettingsStore(Paths.SettingsFile);
        Settings = settings ?? new PlayerSettings();
        Player = new FakePlayer(Settings);
        Vm = new MainViewModel(Settings, Store, Paths, wid => { AttachedWid = wid; return Player; }, Torrents, new ImmediateDispatcher(), ytdlp,
            discEngine ? _ => DiscPlayer = new FakePlayer(Settings) : null)
        {
            Dialogs = Dialogs,
        };
        if (attach) Vm.AttachPlayer(42);
        if (discEngine && attach) Vm.AttachDiscSurface(43);
    }

    public string MediaFile(string name = "movie.mkv")
    {
        var f = Path.Combine(Root, name);
        File.WriteAllText(f, "");
        return f;
    }

    public void Dispose() { }
}

public class KeyboardTests
{
    [Theory]
    [InlineData("SPACE", "TogglePause")]
    [InlineData(".", "FrameStep")]
    [InlineData(",", "FrameBackStep")]
    [InlineData("RIGHT", "SeekRelative 5")]
    [InlineData("LEFT", "SeekRelative -5")]
    [InlineData("Ctrl+RIGHT", "SeekRelative 60")]
    [InlineData("s", "Screenshot False")]
    [InlineData("S", "Screenshot True")]
    [InlineData("m", "ToggleMute")]
    [InlineData("i", "ToggleStats")]
    [InlineData("a", "CycleTrack Audio")]
    [InlineData("j", "CycleTrack Subtitle")]
    [InlineData("v", "ToggleSubtitleVisibility")]
    [InlineData("x", "AdjustSubtitleDelay 0.1")]
    public void Shortcut_invokes_player(string key, string expectedCall)
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        Assert.True(r.Vm.HandleKey(key));
        Assert.Contains(expectedCall, r.Player.Calls);
    }

    [Fact]
    public void Keys_forwarded_from_mpv_are_handled_too()
    {
        using var r = new Rig();
        r.Player.RaiseKey(".");
        Assert.Contains("FrameStep", r.Player.Calls);
    }

    [Fact]
    public void Unknown_keys_are_not_handled()
    {
        using var r = new Rig();
        Assert.False(r.Vm.HandleKey("q"));
        Assert.Empty(r.Player.Calls);
    }

    [Fact]
    public void Volume_keys_change_volume_and_show_osd()
    {
        using var r = new Rig();
        r.Vm.HandleKey("UP");
        Assert.Contains("ChangeVolume 5", r.Player.Calls);
        Assert.Contains(r.Player.Calls, c => c.StartsWith("ShowText Volume 105"));
    }

    [Fact]
    public void Shift_d_toggles_downmix_and_persists()
    {
        using var r = new Rig();
        r.Vm.HandleKey("D");
        Assert.True(r.Settings.DownmixToStereo);
        Assert.Contains("SetDownmix True", r.Player.Calls);
        Assert.True(r.Store.Load().DownmixToStereo, "saved to settings.json next to the exe");
        r.Vm.HandleKey("D");
        Assert.Contains("SetDownmix False", r.Player.Calls);
        Assert.False(r.Store.Load().DownmixToStereo);
    }

    [Fact]
    public void H_toggles_hdr_mode()
    {
        using var r = new Rig();
        r.Vm.HandleKey("h");
        Assert.Equal(HdrMode.Passthrough, r.Settings.HdrMode);
        Assert.Contains("SetHdrMode Passthrough", r.Player.Calls);
        r.Vm.HandleKey("h");
        Assert.Equal(HdrMode.TonemapToSdr, r.Settings.HdrMode);
    }

    [Fact]
    public void Chapters_and_disc_titles_reach_the_menu()
    {
        using var r = new Rig();
        Assert.False(r.Vm.HasChapterMenu);
        r.Vm.IsIdle = false;
        r.Player.Chapters = [new ChapterInfo(0, "Opening", 0), new ChapterInfo(1, "Middle", 120)];
        r.Player.Titles = [new DiscTitleInfo(0, "title: 1 (01:52:00)", true), new DiscTitleInfo(1, "title: 2 (00:03:10)", false)];
        r.Player.State.Chapter = 1;
        r.Player.RaiseChapters();
        Assert.True(r.Vm.HasChapters);
        Assert.True(r.Vm.HasDiscTitles);
        Assert.True(r.Vm.Chapters[1].IsCurrent);
        Assert.Equal("2. Middle  ·  02:00", r.Vm.Chapters[1].Label);
        Assert.True(r.Vm.DiscTitles[0].IsCurrent);

        r.Vm.HandleKey("PGDWN");
        r.Vm.HandleKey("PGUP");
        r.Vm.GoToChapterCommand.Execute(r.Vm.Chapters[0]);
        r.Vm.SelectDiscTitleCommand.Execute(r.Vm.DiscTitles[1]);
        Assert.Equal(["StepChapter 1", "StepChapter -1", "GoToChapter 0", "SelectTitle 1"],
            r.Player.Calls.Where(c => c.Contains("Chapter") || c.StartsWith("SelectTitle")));
    }

    [Fact]
    public void Hdr_menu_shows_only_for_hdr_video()
    {
        using var r = new Rig();
        var changed = new List<string?>();
        r.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        r.Player.State.Transfer = "bt.1886";
        r.Player.RaiseState("video-params/gamma");
        Assert.False(r.Vm.IsHdrVideo);
        r.Player.State.Transfer = "pq";
        r.Player.RaiseState("video-params/gamma");
        Assert.True(r.Vm.IsHdrVideo);
        Assert.Contains(nameof(MainViewModel.IsHdrVideo), changed);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(6, true)]
    [InlineData(8, true)]
    public void Downmix_shows_only_for_surround_audio(int channels, bool shown)
    {
        using var r = new Rig();
        r.Player.State.SourceChannels = channels;
        r.Player.RaiseState("audio-params/channel-count");
        Assert.Equal(shown, r.Vm.IsSurroundAudio);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hardware_decoding_switch_shows_only_where_the_player_says_it_applies(bool applies)
    {
        using var r = new Rig();
        var changed = new List<string?>();
        r.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        r.Player.State.HardwareDecodingApplies = applies;
        r.Player.State.HardwareDecodingOn = applies; // on by default wherever it applies
        r.Player.RaiseState("hardware-decoding");
        Assert.Equal(applies, r.Vm.HardwareDecodingApplies);
        Assert.Equal(applies, r.Vm.HardwareDecoding);
        Assert.Contains(nameof(MainViewModel.HardwareDecodingApplies), changed);
        Assert.Contains(nameof(MainViewModel.HardwareDecoding), changed);
    }

    [Fact]
    public void Hardware_decoding_switch_is_for_the_playing_video_and_isnt_saved()
    {
        using var r = new Rig();
        r.Player.State.HardwareDecodingApplies = true;
        r.Player.State.HardwareDecodingOn = true;
        r.Player.RaiseState("hardware-decoding");
        r.Vm.HardwareDecoding = false;
        Assert.Contains("SetHardwareDecoding False", r.Player.Calls);
        Assert.Contains("ShowText Hardware decoding: off for this video", r.Player.Calls);
        Assert.False(r.Vm.HardwareDecoding);
        Assert.DoesNotContain("ardware", File.Exists(r.Store.FilePath) ? File.ReadAllText(r.Store.FilePath) : "");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(6, false)]
    public void Mono_downmix_shows_only_for_stereo(int channels, bool shown)
    {
        using var r = new Rig();
        r.Player.State.SourceChannels = channels;
        r.Player.RaiseState("audio-params/channel-count");
        Assert.Equal(shown, r.Vm.IsStereoAudio);
    }

    [Fact]
    public void Subtitle_shifts_show_the_total_on_screen()
    {
        using var r = new Rig();
        r.Vm.HandleKey("x");
        Assert.Contains("ShowText Subtitles +0.1s", r.Player.Calls);
        r.Vm.HandleKey("x");
        Assert.Contains("ShowText Subtitles +0.2s", r.Player.Calls);
        r.Vm.HandleKey("z");
        r.Vm.HandleKey("z");
        r.Vm.HandleKey("z");
        Assert.Contains("ShowText Subtitles -0.1s", r.Player.Calls);
    }

    [Fact]
    public void New_audio_and_stream_settings_apply_and_save()
    {
        using var r = new Rig();
        r.Vm.NormalizeAudio = true;
        r.Vm.StreamQualityIndex = 3; // up to 1080p
        r.Vm.SyncToDisplay = true;
        r.Vm.DisplayFpsIndex = 5;    // 144 Hz
        var saved = r.Store.Load();
        Assert.True(saved.NormalizeAudio);
        Assert.Equal(1080, saved.StreamMaxHeight);
        Assert.True(saved.SyncToDisplay);
        Assert.Equal(144, saved.DisplayFps);
        Assert.Contains("ApplyStreamQuality 1080", r.Player.Calls);
        Assert.Contains("ApplyTiming True 0", r.Player.Calls);
        Assert.Contains("ApplyTiming True 144", r.Player.Calls);
    }

    [Fact]
    public void Display_sync_is_off_and_timed_for_the_monitors_own_rate_by_default()
    {
        using var r = new Rig();
        Assert.False(r.Vm.SyncToDisplay);
        Assert.Equal(0, r.Vm.DisplayFpsIndex);
        Assert.Equal(0, MainViewModel.DisplayRates[0]);
    }

    [Fact]
    public void Auto_hdr_follows_the_display()
    {
        using var r = new Rig();
        Assert.True(r.Vm.HdrTonemap);
        r.Vm.DisplayIsHdr = true;
        Assert.Contains("SetDisplayHdr True", r.Player.Calls);
        Assert.True(r.Vm.HdrPassthrough);
        Assert.False(r.Vm.HdrTonemap);
        Assert.Contains("HDR mode", r.Vm.HdrStatusText);
        Assert.Equal(HdrMode.Auto, r.Settings.HdrMode);

        // H overrides by hand: tonemap even on the HDR display.
        r.Vm.HandleKey("h");
        Assert.Equal(HdrMode.TonemapToSdr, r.Settings.HdrMode);
        Assert.True(r.Vm.HdrTonemap);
        r.Vm.HdrModeIndex = 0;
        Assert.Equal(HdrMode.Auto, r.Store.Load().HdrMode);
        Assert.True(r.Vm.HdrPassthrough);
    }

    [Fact]
    public void A_display_found_hdr_before_the_player_exists_is_passed_on()
    {
        using var r = new Rig(attach: false);
        r.Vm.DisplayIsHdr = true;
        r.Vm.AttachPlayer(1);
        Assert.Contains("SetDisplayHdr True", r.Player.Calls);
    }

    [Fact]
    public void D_switches_deinterlacing_for_an_interlaced_video_only()
    {
        using var r = new Rig();
        r.Vm.HandleKey("d");
        Assert.DoesNotContain(r.Player.Calls, c => c.StartsWith("SetDeinterlacing"));
        Assert.Contains("ShowText This video isn't interlaced: nothing to deinterlace", r.Player.Calls);

        r.Player.State.SourceInterlaced = true;
        r.Player.State.DeinterlaceActive = true;
        r.Player.State.DeinterlaceFilter = "BWDIF";
        r.Player.RaiseState("deinterlace-active");
        r.Vm.HandleKey("d");
        Assert.Contains("SetDeinterlacing False", r.Player.Calls);
        Assert.False(r.Vm.Deinterlacing);
        r.Vm.HandleKey("d");
        Assert.Contains("SetDeinterlacing True", r.Player.Calls);
        Assert.True(r.Vm.Deinterlacing);
    }

    [Fact]
    public void Fullscreen_keys_raise_requests()
    {
        using var r = new Rig();
        var requests = new List<bool?>();
        r.Vm.FullscreenRequested += requests.Add;
        r.Vm.HandleKey("f");
        r.Vm.HandleKey("MBTN_LEFT_DBL");
        r.Vm.HandleKey("ESC");
        Assert.Equal([null, null, false], requests);
    }

    [Fact]
    public void Two_quick_clicks_on_the_video_toggle_fullscreen()
    {
        using var r = new Rig();
        var requests = new List<bool?>();
        r.Vm.FullscreenRequested += requests.Add;
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        r.Vm.HandleClick(t);
        r.Vm.HandleClick(t + TimeSpan.FromSeconds(2)); // too slow: a new first click
        Assert.Empty(requests);
        r.Vm.HandleClick(t + TimeSpan.FromSeconds(2.3)); // enter
        Assert.Equal([null], requests);
        r.Vm.HandleClick(t + TimeSpan.FromSeconds(2.5)); // a third quick click doesn't toggle back
        Assert.Single(requests);
        r.Vm.HandleClick(t + TimeSpan.FromSeconds(2.8)); // exit
        Assert.Equal([null, null], requests);
    }

    [Fact]
    public void Mpvs_click_message_reaches_the_double_click_logic()
    {
        using var r = new Rig();
        var requests = new List<bool?>();
        r.Vm.FullscreenRequested += requests.Add;
        r.Player.RaiseClick();
        r.Player.RaiseClick();
        Assert.Equal([null], requests);
    }

    [Fact]
    public void The_pointer_hides_and_returns_with_the_controls()
    {
        using var r = new Rig();
        r.Vm.ControlsVisible = false;
        Assert.Contains("SetCursorHidden True", r.Player.Calls);
        r.Vm.NotifyActivity();
        Assert.Equal("SetCursorHidden False", r.Player.Calls.Last(c => c.StartsWith("SetCursorHidden")));
    }

    [Fact]
    public void Screenshot_is_ignored_while_idle()
    {
        using var r = new Rig();
        r.Vm.HandleKey("s");
        Assert.DoesNotContain(r.Player.Calls, c => c.StartsWith("Screenshot"));
    }

    [Fact]
    public void Screenshot_reports_the_saved_file()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        r.Vm.HandleKey("s");
        Assert.Contains("shot.png", r.Vm.StatusMessage);
    }
}

public class OpeningTests
{
    [Fact]
    public async Task Opening_a_file_plays_it_and_keeps_no_history()
    {
        using var r = new Rig();
        var f = r.MediaFile();
        await r.Vm.OpenInputAsync(f);
        Assert.Contains($"Open {f}", r.Player.Calls);
        r.Vm.SaveSettings();
        Assert.DoesNotContain("recent", File.ReadAllText(r.Store.FilePath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFileName(f), File.ReadAllText(r.Store.FilePath));
    }

    [Fact]
    public async Task Input_received_before_the_video_surface_exists_is_played_once_attached()
    {
        using var r = new Rig(attach: false);
        var f = r.MediaFile();
        await r.Vm.OpenInputAsync(f);
        Assert.Empty(r.Player.Calls);
        r.Vm.AttachPlayer(7);
        Assert.Equal(7, r.AttachedWid);
        Assert.Contains($"Open {f}", r.Player.Calls);
    }

    [Fact]
    public async Task Garbage_input_shows_a_message()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync("definitely not a video");
        Assert.Empty(r.Player.Calls);
        Assert.Contains("isn't a file", r.Vm.StatusMessage);
    }

    [Fact]
    public async Task Open_file_command_uses_the_picker()
    {
        using var r = new Rig();
        r.Dialogs.NextFile = r.MediaFile();
        await r.Vm.OpenFileCommand.ExecuteAsync(null);
        Assert.Contains($"Open {r.Dialogs.NextFile}", r.Player.Calls);
    }

    [Fact]
    public async Task Open_folder_plays_a_dvd_folder_as_a_disc()
    {
        using var r = new Rig();
        var disc = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "paramaja-tests", Guid.NewGuid().ToString("N"), "MOVIE")).FullName;
        var videoTs = Directory.CreateDirectory(Path.Combine(disc, "VIDEO_TS")).FullName;
        File.WriteAllText(Path.Combine(videoTs, "VIDEO_TS.IFO"), "");
        r.Dialogs.NextFolder = disc;
        await r.Vm.OpenFolderCommand.ExecuteAsync(null);
        Assert.Contains($"OpenDisc dvd {disc}", r.Player.Calls);
    }

    [Fact]
    public void Ctrl_shift_o_opens_a_folder()
    {
        Assert.True(ShortcutMap.TryGet(ShortcutMap.Canonical("o", ctrl: true, shift: true), out var action));
        Assert.Equal(PlayerAction.OpenFolder, action);
    }

    [Fact]
    public async Task Paste_and_play_reads_the_clipboard()
    {
        using var r = new Rig();
        r.Dialogs.Clipboard = "https://example.com/stream.m3u8";
        await r.Vm.PasteAndPlayCommand.ExecuteAsync(null);
        Assert.Contains("Open https://example.com/stream.m3u8 [stream]", r.Player.Calls);
    }

    [Fact]
    public async Task Empty_clipboard_is_reported()
    {
        using var r = new Rig();
        await r.Vm.PasteAndPlayCommand.ExecuteAsync(null);
        Assert.Equal("Clipboard is empty.", r.Vm.StatusMessage);
    }

    [Fact]
    public async Task Load_subtitle_adds_the_track()
    {
        using var r = new Rig();
        r.Dialogs.NextSubtitle = "/subs/movie.srt";
        await r.Vm.LoadSubtitleCommand.ExecuteAsync(null);
        Assert.Contains("AddSubtitle /subs/movie.srt", r.Player.Calls);
    }

    [Fact]
    public void Playback_errors_are_surfaced()
    {
        using var r = new Rig();
        r.Player.RaiseEnd(EndFileReason.Error, "unrecognized file format");
        Assert.Contains("unrecognized file format", r.Vm.StatusMessage);
    }
}

public class TorrentTests
{
    private const string Magnet = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel";

    [Fact]
    public async Task Magnet_starts_the_engine_in_the_portable_cache_and_streams_the_chosen_file()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync(Magnet);

        Assert.True(r.Torrents.IsStarted);
        Assert.Equal(r.Paths.TorrentCache, r.Torrents.Config!.CacheDir);
        Assert.Contains($"Add {Magnet}", r.Torrents.Calls);
        Assert.Contains("Open http://127.0.0.1:9/stream/1/1 Sintel.mp4 [stream]", r.Player.Calls);
        Assert.True(r.Vm.IsTorrent);
        Assert.False(r.Vm.IsResolving);
    }

    [Fact]
    public async Task Only_video_files_are_offered_and_the_selection_matches()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync(Magnet);
        Assert.Equal(["Sintel.mp4", "Trailer.mkv"], r.Vm.TorrentFiles.Select(f => f.Name).Order());
        Assert.Equal(1, r.Vm.SelectedTorrentFile!.Index);
        Assert.True(r.Vm.HasTorrentFiles);
        Assert.DoesNotContain(r.Torrents.Calls, c => c.StartsWith("Select"));
    }

    [Fact]
    public async Task Picking_another_file_switches_the_stream()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync(Magnet);
        r.Vm.SelectedTorrentFile = r.Vm.TorrentFiles.Single(f => f.Index == 2);
        Assert.Contains("Select 1 2", r.Torrents.Calls);
        Assert.Contains("Open http://127.0.0.1:9/stream/1/2 Trailer.mkv [stream]", r.Player.Calls);
    }

    [Fact]
    public async Task Bare_info_hash_is_treated_as_a_magnet()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync("08ADA5A7A6183AAE1E09D831DF6748D566095A10");
        Assert.Contains("Add magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10", r.Torrents.Calls);
    }

    [Fact]
    public async Task Failures_are_reported_and_the_ui_recovers()
    {
        using var r = new Rig();
        r.Torrents.OnAdd = _ => throw new TorrentException("timed out resolving the magnet link (no peers found)");
        await r.Vm.OpenInputAsync(Magnet);
        Assert.Contains("no peers found", r.Vm.StatusMessage);
        Assert.False(r.Vm.IsResolving);
        Assert.False(r.Vm.IsTorrent);
        Assert.Empty(r.Player.Calls);
    }

    [Fact]
    public async Task Stats_show_speed_peers_and_progress_of_the_selected_file()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync(Magnet);
        r.Vm.RefreshTorrentStats();
        Assert.Equal("↓ 3.5 MiB/s · 7 peers · 50% downloaded", r.Vm.TorrentStatus);
    }

    [Fact]
    public async Task Opening_a_local_file_stops_the_torrent()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync(Magnet);
        await r.Vm.OpenInputAsync(r.MediaFile());
        Assert.Contains("Remove 1 True", r.Torrents.Calls);
        Assert.False(r.Vm.IsTorrent);
        Assert.Empty(r.Vm.TorrentFiles);
    }

    [Fact]
    public async Task Keeping_the_cache_does_not_delete_files()
    {
        using var r = new Rig(new PlayerSettings { ClearTorrentCacheOnExit = false });
        await r.Vm.OpenInputAsync(Magnet);
        r.Vm.HandleKey("Ctrl+s");
        Assert.Contains("Remove 1 False", r.Torrents.Calls);
        Assert.Contains("Stop", r.Player.Calls);
    }

    [Fact]
    public async Task Dispose_shuts_down_and_clears_the_cache()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync(Magnet);
        Directory.CreateDirectory(Path.Combine(r.Paths.TorrentCache, "abc"));
        File.WriteAllText(Path.Combine(r.Paths.TorrentCache, "abc", "piece"), "x");
        r.Vm.Dispose();
        Assert.True(r.Player.Disposed);
        Assert.Contains("Dispose", r.Torrents.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(r.Paths.TorrentCache));
        Assert.True(File.Exists(r.Paths.SettingsFile));
    }

    [Fact]
    public async Task Leftover_cache_from_a_crash_is_cleared_when_the_engine_starts()
    {
        using var r = new Rig();
        Directory.CreateDirectory(Path.Combine(r.Paths.TorrentCache, "old-torrent"));
        await r.Vm.OpenInputAsync(Magnet);
        Assert.Empty(Directory.EnumerateFileSystemEntries(r.Paths.TorrentCache));
    }

    [Fact]
    public void Dispose_keeps_cache_when_asked()
    {
        using var r = new Rig(new PlayerSettings { ClearTorrentCacheOnExit = false });
        Directory.CreateDirectory(r.Paths.TorrentCache);
        File.WriteAllText(Path.Combine(r.Paths.TorrentCache, "keep"), "x");
        r.Vm.Dispose();
        Assert.True(File.Exists(Path.Combine(r.Paths.TorrentCache, "keep")));
    }
}

public class StateTests
{
    [Fact]
    public void Player_state_flows_into_the_view_model()
    {
        using var r = new Rig();
        var s = r.Player.State;
        s.Position = 3725; s.Duration = 7200; s.IsPaused = true; s.IsIdle = false; s.Title = "Sintel";
        foreach (var p in new[] { "time-pos", "duration", "pause", "idle-active", "media-title" }) r.Player.RaiseState(p);
        Assert.Equal("1:02:05", r.Vm.PositionText);
        Assert.Equal("2:00:00", r.Vm.DurationText);
        Assert.True(r.Vm.IsPaused);
        Assert.False(r.Vm.IsIdle);
        Assert.Equal("Sintel — Paramaja", r.Vm.WindowTitle);
    }

    [Fact]
    public void Hdr_badge_reflects_mode()
    {
        using var r = new Rig();
        r.Player.State.Transfer = "pq";
        r.Player.RaiseState("video-params/gamma");
        Assert.Equal("HDR → SDR", r.Vm.HdrBadge);
        r.Vm.HdrPassthrough = true;
        Assert.Equal("HDR passthrough", r.Vm.HdrBadge);
        r.Player.State.Transfer = "bt.1886";
        r.Player.RaiseState("video-params/gamma");
        Assert.Equal("", r.Vm.HdrBadge);
    }

    [Fact]
    public void Audio_badge_shows_downmix()
    {
        using var r = new Rig();
        r.Player.State.SourceChannels = 6;
        r.Player.State.OutputChannels = 6;
        r.Player.RaiseState("audio-out-params/channel-count");
        Assert.Equal("5.1", r.Vm.AudioBadge);
        // A stereo device folds the channels down, but the player doesn't: the badge says what
        // plays, the tooltip why the speakers get two.
        r.Player.State.OutputChannels = 2;
        r.Player.RaiseState("audio-out-params/channel-count");
        Assert.Equal("5.1", r.Vm.AudioBadge);
        Assert.Contains("Your audio device is set to stereo", r.Vm.AudioTip);
        r.Vm.DownmixToStereo = true;
        Assert.Equal("5.1 → stereo", r.Vm.AudioBadge);
        Assert.Contains("Downmixed to stereo", r.Vm.AudioTip);
        r.Vm.DownmixToStereo = false;
        Assert.Equal("5.1", r.Vm.AudioBadge);
    }

    [Theory]
    [InlineData(8, false, "7.1")]
    [InlineData(8, true, "7.1 → stereo")]
    [InlineData(6, false, "5.1")]
    [InlineData(6, true, "5.1 → stereo")]
    [InlineData(2, true, "stereo")]
    [InlineData(1, true, "mono")]
    public void Audio_badge_follows_the_downmix_switch(int source, bool downmix, string badge)
    {
        using var r = new Rig(new PlayerSettings { DownmixToStereo = downmix });
        r.Player.State.SourceChannels = source;
        r.Player.State.OutputChannels = 2;
        r.Player.RaiseState("audio-params/channel-count");
        Assert.Equal(badge, r.Vm.AudioBadge);
    }

    [Fact]
    public void Audio_tooltip_tells_the_rates()
    {
        using var r = new Rig();
        r.Player.State.SourceChannels = 2;
        r.Player.State.SourceSampleRate = 352_800;
        r.Player.State.OutputSampleRate = 192_000;
        r.Player.RaiseState("audio-params/samplerate");
        Assert.Equal("stereo · 352.8 kHz\nPlayed at 192 kHz", r.Vm.AudioTip);
        r.Player.State.SourceSampleRate = 96_000;
        r.Player.State.OutputSampleRate = 96_000;
        r.Player.RaiseState("audio-out-params/samplerate");
        Assert.Equal("stereo · 96 kHz", r.Vm.AudioTip);
    }

    [Fact]
    public void Decoder_badge()
    {
        using var r = new Rig();
        r.Player.State.HwdecCurrent = "d3d11va";
        r.Player.RaiseState("hwdec-current");
        Assert.Equal("HW · d3d11va", r.Vm.DecoderBadge);
        r.Player.State.HwdecCurrent = "no";
        r.Player.RaiseState("hwdec-current");
        Assert.Equal("SW", r.Vm.DecoderBadge);
    }

    [Theory]
    [InlineData(3840, 2160, "4K")]
    [InlineData(3840, 1600, "4K")]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(1920, 800, "1080p")]
    [InlineData(1280, 720, "720p")]
    [InlineData(720, 480, "480p")]
    public void Resolution_labels(int w, int h, string expected) =>
        Assert.Equal(expected, MainViewModel.ResolutionLabel(w, h));

    [Fact]
    public void Tracks_are_split_with_an_off_option_for_subtitles()
    {
        using var r = new Rig();
        r.Player.Tracks =
        [
            new TrackInfo(1, TrackType.Video, null, null, "hevc", true, false, false, null),
            new TrackInfo(1, TrackType.Audio, "Main", "eng", "truehd", true, false, true, 8),
            new TrackInfo(2, TrackType.Audio, "Commentary", "eng", "ac3", false, false, false, 2),
            new TrackInfo(1, TrackType.Subtitle, "Full", "eng", "subrip", false, false, false, null),
        ];
        r.Player.RaiseTracks();
        Assert.Equal(2, r.Vm.AudioTracks.Count);
        Assert.Equal(2, r.Vm.SubtitleTracks.Count);
        Assert.True(r.Vm.SubtitleTracks[0].IsSelected, "'Off' is selected when no subtitle is");

        r.Vm.SelectAudioCommand.Execute(r.Vm.AudioTracks[1]);
        Assert.Contains("SelectTrack Audio 2", r.Player.Calls);
        r.Vm.SelectSubtitleCommand.Execute(r.Vm.SubtitleTracks[0]);
        Assert.Contains("SelectTrack Subtitle off", r.Player.Calls);
    }

    [Fact]
    public void Volume_slider_sets_player_volume()
    {
        using var r = new Rig();
        r.Vm.Volume = 80;
        Assert.Contains("SetVolume 80", r.Player.Calls);
    }

    [Fact]
    public void Live_settings_apply_and_save()
    {
        using var r = new Rig();
        r.Vm.ToneMapping = "bt.2446a";
        r.Vm.SubtitleScale = 1.5;
        r.Vm.ToneMapping = "not-a-curve";
        Assert.Contains("ApplyHdr", r.Player.Calls);
        Assert.Contains("ApplySubtitles", r.Player.Calls);
        var saved = r.Store.Load();
        Assert.Equal("bt.2446a", saved.ToneMapping);
        Assert.Equal(1.5, saved.SubtitleScale);
    }
}

public class AutoHideTests
{
    [Fact]
    public void Controls_hide_after_inactivity_while_playing()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        r.Vm.NotifyActivity();
        r.Vm.TickAutoHide(DateTime.UtcNow);
        Assert.True(r.Vm.ControlsVisible);
        r.Vm.TickAutoHide(DateTime.UtcNow + MainViewModel.ControlsHideDelay + TimeSpan.FromSeconds(1));
        Assert.False(r.Vm.ControlsVisible);
        r.Player.RaiseMouse();
        Assert.True(r.Vm.ControlsVisible, "mouse movement over the video brings them back");
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Controls_stay_visible_when_idle_paused_or_hovered(bool idle, bool paused, bool hovered)
    {
        using var r = new Rig();
        r.Vm.IsIdle = idle;
        r.Vm.IsPaused = paused;
        r.Vm.IsPointerOverControls = hovered;
        r.Vm.TickAutoHide(DateTime.UtcNow + TimeSpan.FromMinutes(5));
        Assert.True(r.Vm.ControlsVisible);
    }
}

public class OpenLinkDialogTests
{
    [Theory]
    [InlineData("magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10", true, "Magnet link")]
    [InlineData("08ada5a7a6183aae1e09d831df6748d566095a10", true, "Magnet link")]
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ", true, "YouTube")]
    [InlineData("https://youtu.be/aqz-KE-bpKQ", true, "YouTube")]
    [InlineData("https://mega.nz/file/AbC#key", true, "MEGA file")]
    [InlineData("https://example.com/film.mkv", true, "Web link")]
    [InlineData("hello", false, "isn't a link")]
    public void Validates_and_explains_each_kind(string text, bool valid, string hint)
    {
        var vm = new OpenLinkDialogViewModel { Text = text };
        Assert.Equal(valid, vm.IsValid);
        Assert.Contains(hint, vm.Hint);
    }

    [Fact]
    public void Prefills_from_clipboard_only_with_links()
    {
        Assert.StartsWith("https://youtu.be", new OpenLinkDialogViewModel("https://youtu.be/x").Text);
        Assert.Equal("", new OpenLinkDialogViewModel("some text").Text);
        Assert.Equal("", new OpenLinkDialogViewModel().Hint);
    }
}

public class StreamingFeatureTests
{
    [Fact]
    public async Task Web_and_youtube_links_open_as_streams()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync("https://www.youtube.com/watch?v=aqz-KE-bpKQ");
        Assert.Contains("Open https://www.youtube.com/watch?v=aqz-KE-bpKQ [stream]", r.Player.Calls);
        Assert.DoesNotContain(r.Torrents.Calls, c => c.StartsWith("Add"));
    }

    [Fact]
    public async Task Local_files_do_not_use_stream_buffering()
    {
        using var r = new Rig();
        var f = r.MediaFile();
        await r.Vm.OpenInputAsync(f);
        Assert.Contains($"Open {f}", r.Player.Calls);
    }

    [Fact]
    public async Task Mega_links_go_through_the_engine_and_play_decrypted()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync("https://mega.nz/file/AbC#key");
        Assert.True(r.Torrents.IsStarted);
        Assert.Contains("Mega https://mega.nz/file/AbC#key", r.Torrents.Calls);
        Assert.Contains("Open http://127.0.0.1:9/mega/0 Clip.mkv [stream]", r.Player.Calls);
        Assert.Contains("Clip.mkv", r.Vm.StatusMessage);
        Assert.False(r.Vm.IsResolving);
    }

    [Fact]
    public async Task Mega_errors_are_shown()
    {
        using var r = new Rig();
        r.Torrents.OnMega = _ => throw new TorrentException("MEGA says this file doesn't exist");
        await r.Vm.OpenInputAsync("https://mega.nz/file/AbC#key");
        Assert.Equal("MEGA: MEGA says this file doesn't exist", r.Vm.StatusMessage);
        Assert.Empty(r.Player.Calls);
    }

    [Fact]
    public void Buffering_shows_rotating_quips_on_screen_and_keeps_controls_up()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        r.Vm.IsBuffering = true;
        var t0 = DateTime.UtcNow;
        r.Vm.TickAutoHide(t0);
        var first = r.Vm.BufferingText;
        Assert.Contains(first, BufferingQuips.All);
        Assert.Contains($"ShowText {first}", r.Player.Calls);

        r.Vm.TickAutoHide(t0 + TimeSpan.FromSeconds(1));
        Assert.Equal(first, r.Vm.BufferingText);
        r.Vm.TickAutoHide(t0 + MainViewModel.QuipInterval + TimeSpan.FromSeconds(10));
        Assert.NotEqual(first, r.Vm.BufferingText);
        Assert.True(r.Vm.ControlsVisible, "controls stay up while buffering");

        r.Vm.IsBuffering = false;
        r.Vm.TickAutoHide(t0 + TimeSpan.FromMinutes(1));
        Assert.Equal("", r.Vm.BufferingText);
    }

    [Fact]
    public void Resolving_a_link_shows_quips_on_the_welcome_screen()
    {
        using var r = new Rig();
        r.Vm.IsResolving = true;
        r.Vm.TickAutoHide(DateTime.UtcNow);
        Assert.Contains(r.Vm.BufferingText, BufferingQuips.All);
        Assert.DoesNotContain(r.Player.Calls, c => c.StartsWith("ShowText"));
    }

    [Fact]
    public async Task Yt_dlp_update_without_a_bundle_says_so()
    {
        using var r = new Rig();
        Assert.False(r.Vm.HasYtDlp);
        await r.Vm.UpdateYtDlpCommand.ExecuteAsync(null);
        Assert.Contains("isn't bundled", r.Vm.YtDlpStatus);
    }
}

public class PlaybackSettingTests
{
    [Fact]
    public void Volume_is_shown_in_percent_and_decibels()
    {
        using var r = new Rig();
        r.Vm.Volume = 50;
        Assert.Equal("50%", r.Vm.VolumeText);
        Assert.Equal("-30.0 dB", r.Vm.VolumeDecibels);
    }

    [Fact]
    public void Subtitle_slider_shows_the_font_size()
    {
        using var r = new Rig();
        Assert.Equal("Font size 44  (1.00×)", r.Vm.SubtitleSizeText);
        var changed = new List<string?>();
        r.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        r.Vm.SubtitleScale = 1.5;
        Assert.Equal("Font size 66  (1.50×)", r.Vm.SubtitleSizeText);
        Assert.Contains(nameof(MainViewModel.SubtitleSizeText), changed);
    }

    [Fact]
    public void Deinterlace_badge_names_the_filter_and_shows_only_for_interlaced_video()
    {
        using var r = new Rig();
        Assert.False(r.Vm.IsInterlacedVideo);
        r.Player.State.SourceInterlaced = true;
        r.Player.State.InterlaceDetectedBy = PlayerState.ByFlags;
        r.Player.State.DeinterlaceActive = true;
        r.Player.State.DeinterlaceFilter = "BWDIF";
        r.Player.RaiseState("deinterlace-active");
        Assert.True(r.Vm.IsInterlacedVideo);
        Assert.Equal("BWDIF", r.Vm.DeinterlaceBadge);
        Assert.Contains("flagged", r.Vm.DeinterlaceTip);

        // Switched off: the video is still interlaced, and says so.
        r.Vm.Deinterlacing = false;
        Assert.Contains("SetDeinterlacing False", r.Player.Calls);
        Assert.Equal("Interlaced", r.Vm.DeinterlaceBadge);
        Assert.Contains("off for this video", r.Vm.DeinterlaceTip);

        // A progressive video: no badge, no switch.
        r.Player.State.SourceInterlaced = false;
        r.Player.State.DeinterlaceActive = false;
        r.Player.State.DeinterlacingOn = true;
        r.Player.RaiseState("deinterlace-active");
        Assert.Equal("", r.Vm.DeinterlaceBadge);
        Assert.False(r.Vm.IsInterlacedVideo);
        var calls = r.Player.Calls.Count;
        r.Vm.Deinterlacing = false;
        Assert.Equal(calls, r.Player.Calls.Count);
    }

    [Fact]
    public void The_picture_check_is_named_in_the_tip()
    {
        using var r = new Rig();
        r.Player.State.SourceInterlaced = true;
        r.Player.State.InterlaceDetectedBy = PlayerState.ByPicture;
        r.Player.State.DeinterlaceActive = true;
        r.Player.State.DeinterlaceFilter = "BWDIF";
        r.Player.RaiseState("deinterlace-active");
        Assert.Contains("seen in the picture", r.Vm.DeinterlaceTip);
    }

    [Fact]
    public void Codec_badge_names_the_codec()
    {
        using var r = new Rig();
        r.Player.State.VideoFormat = "hevc";
        r.Player.State.VideoCodec = "hevc (HEVC (High Efficiency Video Coding))";
        r.Player.RaiseState("video-format");
        Assert.Equal("HEVC", r.Vm.CodecBadge);
        Assert.Contains("High Efficiency", r.Vm.CodecTip);
        r.Player.State.VideoFormat = "mpeg2video";
        r.Player.State.VideoCodec = null;
        r.Player.RaiseState("video-format");
        Assert.Equal("MPEG-2", r.Vm.CodecBadge);
        Assert.Equal("MPEG-2", r.Vm.CodecTip);
        r.Player.State.VideoFormat = null;
        r.Player.RaiseState("video-format");
        Assert.Equal("", r.Vm.CodecBadge);
    }

    [Fact]
    public void A_stereo_sources_channel_choice_goes_to_the_player_and_resets_with_it()
    {
        using var r = new Rig();
        r.Player.State.SourceChannels = 2;
        r.Player.RaiseState("audio-params/channel-count");
        Assert.True(r.Vm.IsStereoAudio);
        r.Vm.StereoChannelIndex = 1;
        Assert.Contains("SetStereoChannel Left", r.Player.Calls);
        Assert.Contains("ShowText Left channel only (on both speakers)", r.Player.Calls);
        Assert.Equal(1, r.Vm.StereoChannelIndex);
        r.Vm.StereoChannelIndex = 2;
        Assert.Contains("SetStereoChannel Right", r.Player.Calls);

        // The next video starts with both channels (the player resets and says so).
        var changed = new List<string?>();
        r.Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        r.Player.State.StereoChannel = StereoChannel.Both;
        r.Player.RaiseState("stereo-channel");
        Assert.Equal(0, r.Vm.StereoChannelIndex);
        Assert.Contains(nameof(MainViewModel.StereoChannelIndex), changed);
        // Nothing is saved: it's for that video only.
        Assert.DoesNotContain("stereoChannel", File.Exists(r.Store.FilePath) ? File.ReadAllText(r.Store.FilePath) : "");
    }

    [Fact]
    public void Original_audio_by_default_and_passthrough_excludes_downmix()
    {
        using var r = new Rig();
        Assert.False(r.Vm.DownmixToStereo);
        Assert.False(r.Vm.AudioPassthrough);

        r.Vm.DownmixToStereo = true;
        r.Vm.AudioPassthrough = true;
        Assert.False(r.Vm.DownmixToStereo, "a bitstream can't be downmixed");
        Assert.Contains("ApplyAudio", r.Player.Calls);

        r.Vm.DownmixToStereo = true;
        Assert.False(r.Vm.AudioPassthrough);
        Assert.True(r.Store.Load().DownmixToStereo);
    }

    [Fact]
    public void Moving_the_pointer_over_the_window_brings_the_controls_back()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        r.Vm.ReportPointer(100, 100, insideWindow: true);
        r.Vm.TickAutoHide(DateTime.UtcNow + TimeSpan.FromMinutes(1));
        Assert.False(r.Vm.ControlsVisible);

        r.Vm.ReportPointer(100, 100, insideWindow: true);   // not moved
        Assert.False(r.Vm.ControlsVisible);
        r.Vm.ReportPointer(5000, 100, insideWindow: false); // moved, but elsewhere on screen
        Assert.False(r.Vm.ControlsVisible);
        r.Vm.ReportPointer(110, 105, insideWindow: true);
        Assert.True(r.Vm.ControlsVisible);
    }
}

public class EngineStateTests
{
    [Fact]
    public async Task Switching_engines_takes_over_the_new_engines_state()
    {
        using var r = new Rig(discEngine: true);
        var disc = Path.Combine(Path.GetTempPath(), "paramaja-app-tests", Guid.NewGuid().ToString("N"), "MOVIE");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(disc, "VIDEO_TS")).FullName, "VIDEO_TS.IFO"), "");
        await r.Vm.OpenInputAsync(disc);
        Assert.True(r.Vm.IsDiscMode);
        // The disc is paused and interlaced.
        var d = r.DiscPlayer!.State;
        d.IsPaused = true;
        d.SourceInterlaced = d.DeinterlaceActive = true;
        d.DeinterlaceFilter = "BWDIF";
        d.VideoFormat = "mpeg2video";
        foreach (var name in new[] { "pause", "deinterlace-active", "video-format" }) r.DiscPlayer.RaiseState(name);
        Assert.True(r.Vm.IsPaused);
        Assert.Equal("BWDIF", r.Vm.DeinterlaceBadge);
        Assert.Equal("DVD", r.Vm.CodecBadge); // a DVD shows as one, not as MPEG-2
        Assert.Equal("DVD (MPEG-2)", r.Vm.CodecTip);

        // A progressive file plays through mpv, whose pause was already off: no event comes.
        r.Player.State.VideoFormat = "h264";
        await r.Vm.OpenInputAsync(r.MediaFile());
        Assert.False(r.Vm.IsDiscMode);
        Assert.False(r.Vm.IsPaused, "the play button shows pause while the file plays");
        Assert.Equal("", r.Vm.DeinterlaceBadge);
        Assert.False(r.Vm.IsInterlacedVideo);
        Assert.Equal("H.264", r.Vm.CodecBadge);
    }

    [Fact]
    public void A_missed_pause_event_is_healed_by_asking_the_engine()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        r.Vm.IsPaused = true; // a stray "paused" left the play icon up
        r.Player.LivePaused = false;
        r.Vm.TickAutoHide(DateTime.UtcNow);
        Assert.False(r.Vm.IsPaused);
        r.Player.LivePaused = true;
        r.Vm.TickAutoHide(DateTime.UtcNow);
        Assert.True(r.Vm.IsPaused);
        // An engine that can't tell is left alone.
        r.Player.LivePaused = null;
        r.Vm.IsPaused = false;
        r.Vm.TickAutoHide(DateTime.UtcNow);
        Assert.False(r.Vm.IsPaused);
    }

    [Fact]
    public void The_position_shown_is_throttled_and_skipped_while_the_controls_are_hidden()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        r.Player.State.Duration = 600;
        r.Player.RaiseState("duration");
        var changes = 0;
        r.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Position)) changes++; };
        for (var i = 1; i <= 25; i++) // a second of 25 fps video, each frame reporting its position
        {
            r.Player.State.Position = 10 + i * 0.04;
            r.Player.RaiseState("time-pos");
        }
        Assert.InRange(changes, 1, 2);

        // A seek shows at once.
        r.Player.State.Position = 300;
        r.Player.RaiseState("time-pos");
        Assert.Equal(300, r.Vm.Position);

        // Hidden controls: no updates, then the current one as they show.
        r.Vm.ControlsVisible = false;
        changes = 0;
        r.Player.State.Position = 300.5;
        r.Player.RaiseState("time-pos");
        Assert.Equal(0, changes);
        r.Vm.NotifyActivity();
        Assert.Equal(300.5, r.Vm.Position);

        // Pausing shows the exact position.
        r.Player.State.Position = 300.6;
        r.Player.State.IsPaused = true;
        r.Player.RaiseState("pause");
        Assert.Equal(300.6, r.Vm.Position);
    }

    [Fact]
    public void An_open_menu_keeps_the_controls_up()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        var start = DateTime.UtcNow;
        r.Vm.MenuOpened();
        r.Vm.TickAutoHide(start + MainViewModel.ControlsHideDelay * 3);
        Assert.True(r.Vm.ControlsVisible);
        r.Vm.MenuClosed();
        r.Vm.MenuClosed(); // a stray close doesn't count below zero
        Assert.False(r.Vm.IsMenuOpen);
        r.Vm.TickAutoHide(DateTime.UtcNow + MainViewModel.ControlsHideDelay * 2);
        Assert.False(r.Vm.ControlsVisible);
    }

    [Fact]
    public void Create_log_saves_every_recorded_second_to_the_logs_folder()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        r.Player.State.VideoFormat = "hevc";
        r.Player.State.VideoWidth = 3840;
        r.Player.State.VideoHeight = 2160;
        r.Player.Messages.Add("[vo/gpu-next] warn: something to see");
        long drops = 0;
        var at = DateTime.UtcNow;
        r.Player.NextSample = () => new HealthSample(at = at.AddSeconds(1), true, false, drops += 2, 0, 1, null, 4, null, 60)
        {
            Position = (at - DateTime.UtcNow).TotalSeconds,
            OutputFps = 23.976,
            DisplayFps = 60,
            MeasuredDisplayFps = 143.9,
            GpuMicroseconds = 2500,
        };
        for (var i = 0; i < 5; i++) r.Vm.RefreshHealth();
        r.Vm.CreateLogCommand.Execute(null);

        var file = r.Vm.LastLogFile!;
        Assert.StartsWith(r.Paths.Logs, file);
        Assert.Matches(@"paramaja-\d{8}-\d{6}\.log$", file);
        var log = File.ReadAllText(file);
        Assert.Contains("HEVC 3840x2160", log);
        Assert.Contains("Played by: mpv", log);
        Assert.Contains("frame timing: audio clock", log);
        Assert.Contains("143.9", log);                     // the refresh rate mpv measured
        Assert.Contains("Frames: dropped 8", log);         // 5 samples, 2 more each second
        Assert.Contains("GPU time per frame: 2.50 ms", log);
        Assert.Contains("something to see", log);
        Assert.Equal(5, log.Split('\n').Count(l => l.Contains(" playing ")));
        Assert.Contains("Saved", r.Vm.LogStatus);

        // A new video starts a new recording.
        r.Player.RaiseLoaded();
        r.Vm.CreateLogCommand.Execute(null);
        Assert.Contains("nothing recorded yet", File.ReadAllText(r.Vm.LastLogFile!));
    }

    [Fact]
    public void The_high_performance_gpu_is_chosen_only_where_windows_wouldnt_pick_it()
    {
        GpuAdapter Gpu(string name, int order) => new(name, 0x10de, 6L << 30, order);
        // A laptop: the gaming GPU ranks first by performance, but Windows hands out the built-in one.
        Assert.Equal("NVIDIA GeForce RTX 4060 Laptop GPU",
            GpuAdapters.Preferred([Gpu("NVIDIA GeForce RTX 4060 Laptop GPU", 1), Gpu("Intel(R) UHD Graphics", 0)]));
        // A desktop with the monitor on the graphics card, or one GPU: nothing to change.
        Assert.Null(GpuAdapters.Preferred([Gpu("AMD Radeon RX 9060 XT", 0), Gpu("AMD Radeon(TM) Graphics", 1)]));
        Assert.Null(GpuAdapters.Preferred([Gpu("AMD Radeon RX 9060 XT", 0)]));
        Assert.Null(GpuAdapters.Preferred([]));
        Assert.Contains("high-performance", GpuAdapters.Describe([Gpu("A", 1), Gpu("B", 0)]));
        Assert.Contains("Windows' default", GpuAdapters.Describe([Gpu("A", 1), Gpu("B", 0)]));
        // Off Windows there's nothing to list; on Windows (CI) it lists without failing, software adapters left out.
        var adapters = GpuAdapters.List();
        if (!OperatingSystem.IsWindows()) Assert.Empty(adapters);
        Assert.DoesNotContain(adapters, a => a.Description.Contains("Basic Render", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_log_can_be_made_with_nothing_playing()
    {
        using var r = new Rig(attach: false);
        r.Vm.CreateLogCommand.Execute(null);
        Assert.Contains("Nothing is playing", File.ReadAllText(r.Vm.LastLogFile!));
    }
}
