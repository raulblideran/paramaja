using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Paramaja.App.Controls;
using Paramaja.App.Services;
using Paramaja.App.ViewModels;
using Paramaja.App.Views;
using Paramaja.Core.Input;
using Paramaja.Core.Playback;
using Paramaja.Core.Settings;

namespace Paramaja.App.Tests;

public class HeadlessUiTests
{
    [AvaloniaFact]
    public void Controls_bar_renders_and_buttons_drive_the_player()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        var bar = new ControlsBar { DataContext = r.Vm, Width = 1280 };
        bar.Show();
        var buttons = bar.GetVisualDescendants().OfType<Button>().ToList();
        Assert.True(buttons.Count >= 10, $"expected the transport buttons, got {buttons.Count}");

        buttons.First(b => (ToolTip.GetTip(b) as string)?.StartsWith("Play / Pause") == true).Command!.Execute(null);
        buttons.First(b => (ToolTip.GetTip(b) as string)?.StartsWith("Previous frame") == true).Command!.Execute(null);
        Assert.Contains("TogglePause", r.Player.Calls);
        Assert.Contains("FrameBackStep", r.Player.Calls);

        var frame = bar.CaptureRenderedFrame();
        Assert.NotNull(frame);
        bar.Close();
    }

    [AvaloniaFact]
    public void Keys_pressed_on_the_controls_bar_reach_the_view_model()
    {
        using var r = new Rig();
        var bar = new ControlsBar { DataContext = r.Vm, Width = 1000 };
        bar.Show();
        bar.KeyPressQwerty(PhysicalKey.Period, RawInputModifiers.None);
        bar.KeyPressQwerty(PhysicalKey.Comma, RawInputModifiers.None);
        Assert.Contains("FrameStep", r.Player.Calls);
        Assert.Contains("FrameBackStep", r.Player.Calls);
        bar.Close();
    }

    [AvaloniaFact]
    public void Seek_bar_maps_clicks_to_time()
    {
        var seek = new SeekBar { Width = 216, Maximum = 100 };
        var window = new Window { Content = seek, Width = 216, Height = 60 };
        window.Show();
        Assert.Equal(0, seek.TimeAt(0));
        Assert.Equal(50, seek.TimeAt(108), 3);
        Assert.Equal(100, seek.TimeAt(500));

        double? requested = null;
        seek.SeekRequested += t => requested = t;
        var point = seek.TranslatePoint(new Point(108, 20), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.NotNull(requested);
        Assert.Equal(50, requested!.Value, 0);
        window.Close();
    }

    [AvaloniaFact]
    public void Settings_window_toggles_downmix()
    {
        using var r = new Rig();
        var w = new SettingsWindow { DataContext = r.Vm };
        w.Show();
        var tabs = w.GetLogicalDescendants().OfType<TabItem>().Select(t => t.Header as string).ToList();
        Assert.Equal(["Audio", "Subtitles", "Torrents", "Playback", "Developer", "About", "Shortcuts"], tabs);

        w.GetLogicalDescendants().OfType<TabControl>().Single().SelectedIndex = 0;
        Dispatch();
        var switches = w.GetVisualDescendants().OfType<ToggleSwitch>().ToList();
        Assert.DoesNotContain(switches, t => (t.OnContent as string)?.Contains("mono", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(switches, t => (t.OnContent as string)?.Contains("Passthrough") == true); // in Developer now
        var toggle = switches.First(t => (t.OnContent as string)?.Contains("Downmix") == true);
        toggle.IsChecked = true;
        Assert.True(r.Settings.DownmixToStereo);
        Assert.Contains("SetDownmix True", r.Player.Calls);
        w.Close();
    }

    [AvaloniaFact]
    public void Magnet_dialog_enables_play_only_for_magnets()
    {
        var vm = new OpenLinkDialogViewModel();
        var dlg = new OpenLinkDialog(vm);
        dlg.Show();
        var play = dlg.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Play");
        Assert.False(play.IsEnabled);
        vm.Text = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10";
        Dispatch();
        Assert.True(play.IsEnabled);
        dlg.Close();
    }

    [AvaloniaFact]
    public void Advanced_options_live_in_the_developer_tab_only()
    {
        using var r = new Rig();
        var w = new SettingsWindow { DataContext = r.Vm };
        w.Show();
        Dispatch();
        Assert.Empty(w.GetLogicalDescendants().OfType<Expander>()); // no "Advanced" sections left anywhere
        var tabs = w.GetLogicalDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = w.FindControl<TabItem>("DeveloperTab");
        Dispatch();
        Assert.Equal(2, w.FindControl<ComboBox>("RenderQuality")!.SelectedIndex); // Balanced
        var sync = w.FindControl<ToggleSwitch>("SyncToDisplay")!;
        Assert.False(sync.IsChecked);
        sync.IsChecked = true;
        Assert.True(r.Settings.SyncToDisplay);
        Assert.Contains(w.GetVisualDescendants().OfType<ComboBox>(), c => ReferenceEquals(c.ItemsSource, r.Vm.ToneMappingCurves));
        var texts = w.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
        Assert.Contains("Copy diagnostics (Ctrl+I)", texts);
        Assert.Contains("Statistics overlay (I)", texts);

        w.FindControl<Button>("CreateLogButton")!.Command!.Execute(null);
        Assert.NotNull(r.Vm.LastLogFile);
        Assert.True(File.Exists(r.Vm.LastLogFile));
        Assert.StartsWith(r.Paths.Logs, r.Vm.LastLogFile);
        w.Close();
    }

    [AvaloniaFact]
    public void The_menus_have_no_advanced_or_mono_options()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        var bar = new ControlsBar { DataContext = r.Vm, Width = 1280 };
        bar.Show();
        var video = bar.FindControl<Button>("VideoMenuButton")!;
        Assert.Equal("Video", ToolTip.GetTip(video));
        video.Flyout!.ShowAt(video);
        Dispatch();
        Assert.True(r.Vm.IsMenuOpen, "an open menu keeps the bars up");
        // Deinterlacing, hardware decoding and HDR moved to Settings → Developer.
        r.Player.State.SourceInterlaced = r.Player.State.DeinterlaceActive = true;
        r.Player.State.HardwareDecodingApplies = r.Player.State.HardwareDecodingOn = true;
        r.Player.RaiseState("deinterlace-active");
        r.Player.RaiseState("hardware-decoding");
        Dispatch();
        var content = (Control)video.Flyout.GetType().GetProperty("Content")!.GetValue(video.Flyout)!;
        Assert.Empty(content.GetLogicalDescendants().OfType<Expander>());
        Assert.Null(bar.FindControl<StackPanel>("DeinterlaceOptions"));
        Assert.Null(bar.FindControl<StackPanel>("HardwareOptions"));
        Assert.Null(bar.FindControl<StackPanel>("HdrOptions"));
        Assert.DoesNotContain(content.GetLogicalDescendants().OfType<ToggleSwitch>(), t => t.IsVisible);
        video.Flyout.Hide();
        Dispatch();
        Assert.False(r.Vm.IsMenuOpen);

        var audio = bar.GetVisualDescendants().OfType<Button>().First(b => (ToolTip.GetTip(b) as string) == "Audio");
        audio.Flyout!.ShowAt(audio);
        Dispatch();
        r.Player.State.SourceChannels = 2;
        r.Player.RaiseState("audio-params/channel-count");
        Dispatch();
        var stereo = bar.FindControl<StackPanel>("StereoOptions")!;
        Assert.True(stereo.IsVisible);
        Assert.Empty(stereo.GetLogicalDescendants().OfType<ToggleSwitch>()); // no mono downmix
        var channels = bar.FindControl<ComboBox>("StereoChannel")!;
        Assert.Equal(["Both channels (default)", "Left channel only", "Right channel only"],
            channels.Items.OfType<ComboBoxItem>().Select(i => i.Content as string));
        channels.SelectedIndex = 2;
        Assert.Contains("SetStereoChannel Right", r.Player.Calls);
        var audioContent = (Control)audio.Flyout.GetType().GetProperty("Content")!.GetValue(audio.Flyout)!;
        Assert.DoesNotContain(audioContent.GetLogicalDescendants().OfType<ToggleSwitch>(),
            t => (t.OnContent as string)?.Contains("Passthrough") == true);
        audio.Flyout.Hide();
        bar.Close();
    }

    [AvaloniaFact]
    public void Developer_tab_has_this_videos_switches_where_they_apply()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        var w = new SettingsWindow { DataContext = r.Vm };
        w.Show();
        w.GetLogicalDescendants().OfType<TabControl>().Single().SelectedItem = w.FindControl<TabItem>("DeveloperTab");
        Dispatch();
        var deinterlace = w.FindControl<StackPanel>("DeinterlaceOptions")!;
        var hardware = w.FindControl<StackPanel>("HardwareOptions")!;
        Assert.False(deinterlace.IsVisible);
        Assert.False(hardware.IsVisible);

        r.Player.State.SourceInterlaced = r.Player.State.DeinterlaceActive = true;
        r.Player.State.DeinterlaceFilter = "BWDIF";
        r.Player.RaiseState("deinterlace-active");
        Dispatch();
        Assert.True(deinterlace.IsVisible);
        Assert.Empty(deinterlace.GetLogicalDescendants().OfType<ComboBox>()); // on / off only
        var toggle = w.FindControl<ToggleSwitch>("DeinterlaceSwitch")!;
        Assert.True(toggle.IsChecked);
        toggle.IsChecked = false;
        Assert.Contains("SetDeinterlacing False", r.Player.Calls);

        r.Player.State.HardwareDecodingApplies = r.Player.State.HardwareDecodingOn = true;
        r.Player.RaiseState("hardware-decoding");
        Dispatch();
        Assert.True(hardware.IsVisible);
        w.FindControl<ToggleSwitch>("HardwareSwitch")!.IsChecked = false;
        Assert.Contains("SetHardwareDecoding False", r.Player.Calls);

        // HDR mode, passthrough and the default streaming quality are here too.
        w.FindControl<ComboBox>("HdrMode")!.SelectedIndex = 1;
        Assert.Equal(HdrMode.TonemapToSdr, r.Settings.HdrMode);
        w.FindControl<ToggleSwitch>("PassthroughSwitch")!.IsChecked = true;
        Assert.True(r.Settings.AudioPassthrough);
        w.FindControl<ComboBox>("StreamQualityDefault")!.SelectedIndex = 3;
        Assert.Equal(1080, r.Settings.StreamMaxHeight);
        w.Close();
    }

    [AvaloniaFact]
    public void The_codec_shows_as_a_badge()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        var bar = new ControlsBar { DataContext = r.Vm, Width = 1280 };
        bar.Show();
        var badge = bar.FindControl<Border>("CodecBadge")!;
        Assert.False(badge.IsVisible);
        r.Player.State.VideoFormat = "h264";
        r.Player.RaiseState("video-format");
        Dispatch();
        Assert.True(badge.IsVisible);
        Assert.Equal("H.264", badge.GetLogicalDescendants().OfType<TextBlock>().Single().Text);
        bar.Close();
    }

    [AvaloniaFact]
    public void Settings_window_has_no_bob_and_streaming_quality_left_playback()
    {
        using var r = new Rig();
        var w = new SettingsWindow { DataContext = r.Vm };
        w.Show();
        Dispatch();
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<ComboBox>().SelectMany(c => c.Items.OfType<ComboBoxItem>()),
            i => (i.Content as string)?.StartsWith("Bob") == true);
        var tabs = w.GetLogicalDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Header as string == "Playback");
        Dispatch();
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<ComboBox>(), c => c.Name == "StreamQualityDefault" && c.IsEffectivelyVisible);
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<ComboBox>().SelectMany(c => c.Items.OfType<ComboBoxItem>()),
            i => (i.Content as string)?.StartsWith("Up to 1080p") == true);
        w.Close();
    }

    [AvaloniaFact]
    public void Welcome_screen_is_shown_while_idle()
    {
        using var r = new Rig(attach: false);
        var w = new MainWindow { DataContext = r.Vm };
        w.Show();
        var welcome = w.FindControl<Grid>("Welcome")!;
        Assert.True(welcome.IsVisible);
        Assert.DoesNotContain(welcome.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Recent");
        Assert.Empty(welcome.GetLogicalDescendants().OfType<ItemsControl>());
        r.Vm.IsIdle = false;
        Assert.False(welcome.IsVisible);
        w.Close();
    }

    private static List<string?> MenuHeaders(Button button) =>
        ((MenuFlyout)button.Flyout!).Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();

    private static string? ButtonText(Button b) =>
        b.Content as string ?? (b.Content as Panel)?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault()?.Text;

    [AvaloniaFact]
    public void Welcome_screen_offers_open_open_link_and_download_video_but_no_paste()
    {
        using var r = new Rig(attach: false);
        var w = new MainWindow { DataContext = r.Vm };
        w.Show();
        var texts = w.FindControl<Grid>("Welcome")!.GetLogicalDescendants().OfType<Button>().Select(ButtonText).ToList();
        Assert.Contains("Open", texts);
        Assert.Contains("Open Link", texts);
        Assert.Contains("Download Video", texts);
        Assert.DoesNotContain("Paste", texts);
        Assert.Equal(["Open file", "Open folder", "Open Disc", "Rip Disc…"], MenuHeaders(w.FindControl<Button>("OpenMenu")!));
        w.Close();
    }

    [AvaloniaFact]
    public void Controls_bar_open_menu_has_no_link_or_clipboard_and_download_video_is_its_own_button()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        var bar = new ControlsBar { DataContext = r.Vm, Width = 1280 };
        bar.Show();
        Assert.Equal(["Open file", "Open folder", "Open Disc", "Rip Disc…", "Screenshots folder"], MenuHeaders(bar.FindControl<Button>("OpenMenuButton")!));
        var download = bar.FindControl<Button>("DownloadVideoButton")!;
        Assert.StartsWith("Download Video", ToolTip.GetTip(download) as string);
        Assert.Same(r.Vm.OpenDownloaderCommand, download.Command);
        bar.Close();
    }

    [AvaloniaFact]
    public void Stop_and_the_frame_buttons_and_a_torrents_previous_and_next()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        var bar = new ControlsBar { DataContext = r.Vm, Width = 1280 };
        bar.Show();
        bar.FindControl<Button>("StopButton")!.Command!.Execute(null);
        Assert.Contains("Stop", r.Player.Calls);

        // Audio alone: no frames to step through, and no video menu.
        Assert.True(bar.FindControl<Button>("FrameBackButton")!.IsVisible);
        r.Player.State.HasVideo = false;
        r.Player.RaiseState("has-video");
        Dispatch();
        Assert.True(r.Vm.IsAudioOnly);
        Assert.False(bar.FindControl<Button>("FrameBackButton")!.IsVisible);
        Assert.False(bar.FindControl<Button>("FrameNextButton")!.IsVisible);
        Assert.False(bar.FindControl<Button>("VideoMenuButton")!.IsVisible);
        r.Player.State.HasVideo = true;
        r.Player.RaiseState("has-video");
        Dispatch();
        Assert.True(bar.FindControl<Button>("FrameNextButton")!.IsVisible);

        Assert.False(bar.FindControl<Button>("NextFileButton")!.IsVisible);
        bar.Close();
    }

    [AvaloniaFact]
    public void Video_menu_offers_resize_and_crop_by_the_pictures_shape()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        var bar = new ControlsBar { DataContext = r.Vm, Width = 1280 };
        bar.Show();
        var video = bar.FindControl<Button>("VideoMenuButton")!;
        video.Flyout!.ShowAt(video);
        Dispatch();
        var picture = bar.FindControl<StackPanel>("PictureOptions")!;
        Assert.False(picture.IsVisible); // shape not known yet

        // 16:9: it can be resized to 4:3, and there's nothing to crop.
        r.Player.State.DisplayAspect = 16.0 / 9;
        r.Player.RaiseState("video-params/aspect");
        Dispatch();
        Assert.True(picture.IsVisible);
        Assert.True(bar.FindControl<ToggleSwitch>("ResizeSwitch")!.IsVisible);
        Assert.False(bar.FindControl<ToggleSwitch>("CropSwitch")!.IsVisible);

        // 4:3: it can be cropped to 16:9, not resized to what it is.
        r.Player.State.DisplayAspect = 4.0 / 3;
        r.Player.RaiseState("video-params/aspect");
        Dispatch();
        Assert.False(bar.FindControl<ToggleSwitch>("ResizeSwitch")!.IsVisible);
        var crop = bar.FindControl<ToggleSwitch>("CropSwitch")!;
        Assert.True(crop.IsVisible);
        crop.IsChecked = true;
        Assert.Contains("SetPictureFit Crop16x9", r.Player.Calls);

        // Scope (2.39:1) offers both; one turns the other off.
        r.Player.State.DisplayAspect = 2.39;
        r.Player.RaiseState("video-params/aspect");
        Dispatch();
        bar.FindControl<ToggleSwitch>("ResizeSwitch")!.IsChecked = true;
        Assert.Equal(PictureFit.Resize4x3, r.Player.State.PictureFit);
        Dispatch();
        Assert.False(crop.IsChecked);
        video.Flyout.Hide();
        bar.Close();
    }

    [AvaloniaFact]
    public void Downloader_has_no_get_qualities_button()
    {
        using var r = new Rig();
        var w = new DownloadDialog { DataContext = new DownloadDialogViewModel(r.Vm) };
        w.Show();
        var buttons = w.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
        Assert.DoesNotContain("Get qualities", buttons);
        Assert.Contains("Download", buttons);
        Assert.Equal("Download Video", w.Title);
        w.Close();
    }

    [AvaloniaFact]
    public void Window_and_app_icons_are_embedded()
    {
        using var ico = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Paramaja/Assets/paramaja.ico"));
        Assert.True(ico.Length > 1000);
        using var png = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Paramaja/Assets/paramaja-256.png"));
        Assert.True(png.Length > 1000);
    }

    private static void Dispatch() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}

public class KeyMappingTests
{
    [Theory]
    [InlineData(Key.Space, KeyModifiers.None, "SPACE")]
    [InlineData(Key.OemPeriod, KeyModifiers.None, ".")]
    [InlineData(Key.OemComma, KeyModifiers.None, ",")]
    [InlineData(Key.S, KeyModifiers.None, "s")]
    [InlineData(Key.S, KeyModifiers.Shift, "S")]
    [InlineData(Key.D, KeyModifiers.Shift, "D")]
    [InlineData(Key.V, KeyModifiers.Control, "Ctrl+v")]
    [InlineData(Key.Right, KeyModifiers.Control, "Ctrl+RIGHT")]
    [InlineData(Key.Escape, KeyModifiers.None, "ESC")]
    [InlineData(Key.F12, KeyModifiers.None, null)]
    public void Avalonia_keys_map_to_canonical_names(Key key, KeyModifiers mods, string? expected) =>
        Assert.Equal(expected, KeyMapping.ToCanonical(key, mods));

    [Fact]
    public void Shortcut_actions_are_humanized() =>
        Assert.Equal("Frame back step", SettingsWindow.Humanize(PlayerAction.FrameBackStep));
}
