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
        Assert.Equal(["Video", "Audio", "Subtitles", "Torrents", "Playback", "About", "Shortcuts"], tabs);

        w.GetLogicalDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
        Dispatch();
        var toggle = w.GetVisualDescendants().OfType<ToggleSwitch>().First(t => (t.OnContent as string)?.Contains("Downmix") == true);
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
    public void Tonemapping_and_rendering_are_advanced_and_tonemapping_hides_on_an_hdr_display()
    {
        using var r = new Rig();
        var w = new SettingsWindow { DataContext = r.Vm };
        w.Show();
        Dispatch();
        var tonemap = w.FindControl<Expander>("TonemapAdvanced")!;
        var rendering = w.FindControl<Expander>("RenderingAdvanced")!;
        Assert.True(tonemap.IsVisible);
        Assert.False(tonemap.IsExpanded);
        Assert.False(rendering.IsExpanded);
        r.Vm.DisplayIsHdr = true;
        Dispatch();
        Assert.False(tonemap.IsVisible);
        Assert.True(rendering.IsVisible);
    }

    [AvaloniaFact]
    public void Settings_window_offers_passthrough_and_no_bob()
    {
        using var r = new Rig();
        var w = new SettingsWindow { DataContext = r.Vm };
        w.Show();
        Dispatch();
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<ComboBox>().SelectMany(c => c.Items.OfType<ComboBoxItem>()),
            i => (i.Content as string)?.StartsWith("Bob") == true);

        w.GetLogicalDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
        Dispatch();
        var passthrough = w.GetVisualDescendants().OfType<ToggleSwitch>().First(t => (t.OnContent as string)?.Contains("Passthrough") == true);
        passthrough.IsChecked = true;
        Assert.True(r.Settings.AudioPassthrough);
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
        Assert.Equal(["Open file", "Open folder", "Open Disc"], MenuHeaders(w.FindControl<Button>("OpenMenu")!));
        w.Close();
    }

    [AvaloniaFact]
    public void Controls_bar_open_menu_has_no_link_or_clipboard_and_download_video_is_its_own_button()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        var bar = new ControlsBar { DataContext = r.Vm, Width = 1280 };
        bar.Show();
        Assert.Equal(["Open file", "Open folder", "Open Disc", "Screenshots folder"], MenuHeaders(bar.FindControl<Button>("OpenMenuButton")!));
        var download = bar.FindControl<Button>("DownloadVideoButton")!;
        Assert.StartsWith("Download Video", ToolTip.GetTip(download) as string);
        Assert.Same(r.Vm.OpenDownloaderCommand, download.Command);
        bar.Close();
    }

    [AvaloniaFact]
    public void Hardware_switch_is_in_the_video_menu_only_where_it_applies_and_not_in_settings()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        var bar = new ControlsBar { DataContext = r.Vm, Width = 1280 };
        bar.Show();
        // The video menu's content takes its bindings once the menu opens.
        var video = bar.GetVisualDescendants().OfType<Button>().First(b => (ToolTip.GetTip(b) as string)?.StartsWith("Video") == true);
        video.Flyout!.ShowAt(video);
        Dispatch();
        var hardware = bar.FindControl<StackPanel>("HardwareOptions")!;
        Assert.False(hardware.IsVisible);
        r.Player.State.HardwareDecodingApplies = r.Player.State.HardwareDecodingOn = true;
        r.Player.RaiseState("hardware-decoding");
        Dispatch();
        Assert.True(hardware.IsVisible);
        video.Flyout.Hide();
        bar.Close();

        var w = new SettingsWindow { DataContext = r.Vm };
        w.Show();
        Dispatch();
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<ToggleSwitch>(),
            t => (t.OnContent as string)?.Contains("Hardware") == true);
        w.Close();
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
