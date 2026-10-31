using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Paramaja.App.Views;
using Paramaja.Core.Playback;
using Paramaja.Core.Settings;

namespace Paramaja.App.Tests;

public class Prompt8ViewModelTests
{
    internal static string CdImage(Rig r)
    {
        File.WriteAllBytes(Path.Combine(r.Root, "Dangerous.bin"), new byte[2352 * 4]);
        var cue = Path.Combine(r.Root, "Dangerous.cue");
        File.WriteAllText(cue, "FILE \"Dangerous.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n");
        return cue;
    }

    internal static string SacdImage(Rig r)
    {
        var iso = Path.Combine(r.Root, "Thriller.iso");
        var data = new byte[600 * 2048];
        "SACDMTOC"u8.CopyTo(data.AsSpan(510 * 2048));
        File.WriteAllBytes(iso, data);
        return iso;
    }

    internal static void Play(FakePlayer p)
    {
        p.State.IsIdle = false;
        p.RaiseState("idle-active");
    }

    [Fact]
    public async Task An_audio_cd_shows_as_cd_and_an_sacd_as_sacd()
    {
        using var r = new Rig(discEngine: true);
        await r.Vm.OpenInputAsync(CdImage(r));
        Assert.Contains(r.DiscPlayer!.Calls, c => c.StartsWith("OpenAudioCd"));
        Play(r.DiscPlayer);
        Assert.Equal("CD", r.Vm.CodecBadge);
        Assert.Contains("Audio CD", r.Vm.CodecTip);

        await r.Vm.OpenInputAsync(SacdImage(r));
        r.Player.Titles = [new DiscTitleInfo(1, "Stereo (42:19)", true), new DiscTitleInfo(2, "Multichannel (42:19)", false)];
        r.Player.RaiseChapters();
        Play(r.Player);
        Assert.Equal("SACD", r.Vm.CodecBadge);
        Assert.Equal("Super Audio CD · DSD64 (2.8 MHz) · stereo area", r.Vm.CodecTip);

        // The next file shows by its codec again.
        await r.Vm.OpenInputAsync(r.MediaFile());
        r.Player.State.VideoFormat = "h264";
        r.Player.RaiseState("video-format");
        Assert.Equal("H.264", r.Vm.CodecBadge);
    }

    [Fact]
    public async Task Nothing_shows_once_the_cd_stops()
    {
        using var r = new Rig(discEngine: true);
        await r.Vm.OpenInputAsync(CdImage(r));
        Play(r.DiscPlayer!);
        r.DiscPlayer!.State.IsIdle = true;
        r.DiscPlayer.RaiseState("idle-active");
        Assert.Equal("", r.Vm.CodecBadge);
    }

    [Fact]
    public void Chapter_buttons_need_two_chapters_in_something_playing()
    {
        using var r = new Rig();
        Play(r.Player);
        r.Player.Chapters = [new ChapterInfo(0, "Opening", 0), new ChapterInfo(1, "Middle", 120)];
        r.Player.RaiseChapters();
        Assert.True(r.Vm.HasChapters);

        // A file without chapters.
        r.Player.Chapters = [];
        r.Player.RaiseChapters();
        Assert.False(r.Vm.HasChapters);

        // One chapter is nothing to step through.
        r.Player.Chapters = [new ChapterInfo(0, "Track 1", double.NaN)];
        r.Player.RaiseChapters();
        Assert.False(r.Vm.HasChapters);

        // Stopped: whatever the engine still lists doesn't show.
        r.Player.Chapters = [new ChapterInfo(0, "Opening", 0), new ChapterInfo(1, "Middle", 120)];
        r.Player.RaiseChapters();
        r.Player.State.IsIdle = true;
        r.Player.RaiseState("idle-active");
        Assert.False(r.Vm.HasChapters);
        Assert.False(r.Vm.HasChapterMenu);
    }

    [Fact]
    public async Task A_discs_chapters_go_when_a_file_follows()
    {
        using var r = new Rig(discEngine: true);
        var disc = Path.Combine(r.Root, "CONCERT");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(disc, "VIDEO_TS")).FullName, "VIDEO_TS.IFO"), "");
        await r.Vm.OpenInputAsync(disc);
        Play(r.DiscPlayer!);
        r.DiscPlayer!.Chapters = [new ChapterInfo(0, "A", 0), new ChapterInfo(1, "B", 60)];
        r.DiscPlayer.RaiseChapters();
        Assert.True(r.Vm.HasChapters);
        await r.Vm.OpenInputAsync(r.MediaFile());
        Play(r.Player);
        Assert.False(r.Vm.HasChapters); // mpv's own list: empty
    }

    [Fact]
    public void In_fullscreen_the_pointer_hides_after_3_seconds_even_paused()
    {
        using var r = new Rig();
        Play(r.Player);
        r.Vm.IsFullscreen = true;
        r.Vm.NotifyPointerMoved();
        r.Player.State.IsPaused = true;
        r.Player.RaiseState("pause");
        var now = DateTime.UtcNow;
        r.Vm.TickAutoHide(now + TimeSpan.FromSeconds(2.5));
        Assert.False(r.Vm.IsCursorHidden);
        r.Vm.TickAutoHide(now + TimeSpan.FromSeconds(3.2));
        Assert.True(r.Vm.IsCursorHidden);
        Assert.Equal("SetCursorHidden True", r.Player.Calls.Last(c => c.StartsWith("SetCursorHidden")));
        Assert.True(r.Vm.ControlsVisible); // paused: the controls stay, the pointer goes

        // A key shows the controls, not the pointer; moving the mouse does.
        r.Vm.HandleKey("RIGHT");
        r.Vm.TickAutoHide(DateTime.UtcNow + TimeSpan.FromSeconds(3.2));
        Assert.True(r.Vm.IsCursorHidden);
        r.Vm.NotifyPointerMoved();
        Assert.False(r.Vm.IsCursorHidden);
        Assert.Equal("SetCursorHidden False", r.Player.Calls.Last(c => c.StartsWith("SetCursorHidden")));
    }

    [Fact]
    public void The_pointer_stays_on_the_bars_menus_and_the_welcome_screen()
    {
        using var r = new Rig();
        r.Vm.IsFullscreen = true;
        var later = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        r.Vm.TickAutoHide(later);
        Assert.False(r.Vm.IsCursorHidden); // nothing plays
        Play(r.Player);
        r.Vm.MenuOpened();
        r.Vm.TickAutoHide(later + TimeSpan.FromSeconds(10));
        Assert.False(r.Vm.IsCursorHidden);
        r.Vm.MenuClosed();
        r.Vm.IsPointerOverControls = true;
        r.Vm.TickAutoHide(later + TimeSpan.FromSeconds(20));
        Assert.False(r.Vm.IsCursorHidden);
        r.Vm.IsPointerOverControls = false;
        r.Vm.TickAutoHide(later + TimeSpan.FromSeconds(30));
        Assert.True(r.Vm.IsCursorHidden);
        // Out of fullscreen it goes with the controls.
        r.Vm.IsFullscreen = false;
        Assert.Equal(!r.Vm.ControlsVisible, r.Vm.IsCursorHidden);
    }

    [Fact]
    public async Task An_engine_taking_over_gets_the_pointer_state()
    {
        using var r = new Rig(discEngine: true);
        r.Vm.ControlsVisible = false; // windowed: hidden with the controls
        Assert.True(r.Vm.IsCursorHidden);
        var disc = Path.Combine(r.Root, "CONCERT");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(disc, "VIDEO_TS")).FullName, "VIDEO_TS.IFO"), "");
        await r.Vm.OpenInputAsync(disc);
        Assert.Contains("SetCursorHidden True", r.DiscPlayer!.Calls);
    }

    [Fact]
    public void Exclusive_audio_is_a_saved_setting_applied_live()
    {
        using var r = new Rig();
        Assert.False(r.Vm.ExclusiveAudio);
        r.Vm.ExclusiveAudio = true;
        Assert.True(r.Settings.ExclusiveAudio);
        Assert.Contains("ApplyAudio", r.Player.Calls);
        Assert.True(new SettingsStore(r.Paths.SettingsFile).Load().ExclusiveAudio);
    }
}

public class Prompt8UiTests
{
    [AvaloniaFact]
    public async Task Cd_and_sacd_show_in_the_controls_bars_codec_badge_whatever_the_downmix()
    {
        using var r = new Rig(discEngine: true);
        var bar = new ControlsBar { DataContext = r.Vm, Width = 1280 };
        bar.Show();
        string Text(string name) => (string?)((TextBlock)bar.FindControl<Border>(name)!.Child!).Text ?? "";

        await r.Vm.OpenInputAsync(Prompt8ViewModelTests.CdImage(r));
        Prompt8ViewModelTests.Play(r.DiscPlayer!);
        r.DiscPlayer!.State.SourceChannels = 2;
        r.DiscPlayer.RaiseState("audio-params/channel-count");
        Dispatch();
        Assert.True(bar.FindControl<Border>("CodecBadge")!.IsVisible);
        Assert.Equal("CD", Text("CodecBadge"));
        Assert.Equal("stereo", Text("AudioBadge"));

        await r.Vm.OpenInputAsync(Prompt8ViewModelTests.SacdImage(r));
        Prompt8ViewModelTests.Play(r.Player);
        r.Player.State.SourceChannels = 6;
        r.Player.RaiseState("audio-params/channel-count");
        Dispatch();
        Assert.Equal("SACD", Text("CodecBadge"));
        Assert.Equal("5.1", Text("AudioBadge"));

        // The downmix switch is the channels badge's business only.
        r.Vm.DownmixToStereo = true;
        Dispatch();
        Assert.Equal("SACD", Text("CodecBadge"));
        Assert.Equal("5.1 → stereo", Text("AudioBadge"));
        r.Vm.DownmixToStereo = false;
        Dispatch();
        Assert.Equal("SACD", Text("CodecBadge"));
        Assert.Equal("5.1", Text("AudioBadge"));
        bar.Close();
    }

    [AvaloniaFact]
    public void Chapter_buttons_show_only_with_chapters()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        var bar = new ControlsBar { DataContext = r.Vm, Width = 1280 };
        bar.Show();
        Assert.False(bar.FindControl<Button>("PreviousChapterButton")!.IsVisible);
        Assert.False(bar.FindControl<Button>("NextChapterButton")!.IsVisible);
        r.Player.Chapters = [new ChapterInfo(0, "Opening", 0), new ChapterInfo(1, "Middle", 120)];
        r.Player.RaiseChapters();
        Dispatch();
        Assert.True(bar.FindControl<Button>("PreviousChapterButton")!.IsVisible);
        Assert.True(bar.FindControl<Button>("NextChapterButton")!.IsVisible);
        r.Player.Chapters = [];
        r.Player.RaiseChapters();
        Dispatch();
        Assert.False(bar.FindControl<Button>("NextChapterButton")!.IsVisible);
        bar.Close();
    }

    [AvaloniaFact]
    public void The_audio_badge_has_its_tooltip()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        r.Player.State.SourceChannels = 8;
        r.Player.State.OutputChannels = 2;
        r.Player.RaiseState("audio-params/channel-count");
        var bar = new ControlsBar { DataContext = r.Vm, Width = 1280 };
        bar.Show();
        var badge = bar.FindControl<Border>("AudioBadge")!;
        Assert.Equal("7.1", ((TextBlock)badge.Child!).Text);
        Assert.Contains("mixes this 7.1 down", ToolTip.GetTip(badge) as string);
        bar.Close();
    }

    [AvaloniaFact]
    public void The_exclusive_switch_is_in_the_audio_tab_on_windows()
    {
        using var r = new Rig();
        var w = new SettingsWindow { DataContext = r.Vm };
        w.Show();
        var exclusive = w.FindControl<ToggleSwitch>("ExclusiveSwitch")!;
        Assert.Equal(OperatingSystem.IsWindows(), exclusive.IsEffectivelyVisible);
        exclusive.IsChecked = true;
        Assert.True(r.Vm.ExclusiveAudio);
        w.Close();
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
