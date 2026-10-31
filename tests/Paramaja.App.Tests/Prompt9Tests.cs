using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Paramaja.App.Services;
using Paramaja.App.ViewModels;
using Paramaja.App.Views;
using Paramaja.Core.Ripping;
using Paramaja.Core.Ripping.Dvd;

namespace Paramaja.App.Tests;

public class Prompt9DropTests
{
    [Fact]
    public async Task Dropped_files_open_the_first_and_play_the_rest_after_it()
    {
        using var r = new Rig();
        var (a, b, c) = (r.MediaFile("a.mkv"), r.MediaFile("b.mkv"), r.MediaFile("c.mkv"));
        await r.Vm.DropAsync([a, b, c], null);
        Assert.Contains($"Open {a}", r.Player.Calls);
        Assert.Equal([$"Append {b}", $"Append {c}"], r.Player.Calls.Where(x => x.StartsWith("Append")));
    }

    [Fact]
    public async Task A_subtitle_dropped_with_a_video_joins_it_once_it_has_loaded()
    {
        using var r = new Rig();
        var (video, subs) = (r.MediaFile("concert.mkv"), r.MediaFile("concert.en.srt"));
        await r.Vm.DropAsync([subs, video], null);
        Assert.Contains($"Open {video}", r.Player.Calls);
        Assert.DoesNotContain(r.Player.Calls, x => x.StartsWith("AddSubtitle"));
        r.Player.RaiseLoaded();
        Assert.Contains($"AddSubtitle {subs}", r.Player.Calls);
    }

    [Fact]
    public async Task A_subtitle_dropped_alone_joins_the_video_playing_and_needs_one()
    {
        using var r = new Rig();
        var subs = r.MediaFile("concert.ass");
        await r.Vm.DropAsync([subs], null);
        Assert.DoesNotContain(r.Player.Calls, x => x.StartsWith("AddSubtitle") || x.StartsWith("Open"));
        Assert.Contains("subtitles join", r.Vm.StatusMessage);

        await r.Vm.OpenInputAsync(r.MediaFile());
        Prompt8ViewModelTests.Play(r.Player);
        await r.Vm.DropAsync([subs], null);
        Assert.Contains($"AddSubtitle {subs}", r.Player.Calls);
    }

    [Fact]
    public async Task A_link_dropped_from_a_browser_opens()
    {
        using var r = new Rig();
        await r.Vm.DropAsync([], "https://example.com/concert.mp4");
        Assert.Contains("Open https://example.com/concert.mp4 [stream]", r.Player.Calls);
    }

    [Fact]
    public void A_uri_list_gives_local_paths_or_else_a_link()
    {
        var (paths, text) = X11DragDrop.ParseUriList("# from Dolphin\r\nfile:///home/me/Live%20at%20Wembley.mkv\r\nfile:///home/me/subs.srt\r\n");
        // (Windows, where CI runs this too, writes the same path with backslashes.)
        Assert.Equal(["/home/me/Live at Wembley.mkv", "/home/me/subs.srt"], paths.Select(p => p.Replace('\\', '/')));
        Assert.Null(text);
        (paths, text) = X11DragDrop.ParseUriList("https://www.youtube.com/watch?v=aqz-KE-bpKQ\r\n");
        Assert.Empty(paths);
        Assert.Equal("https://www.youtube.com/watch?v=aqz-KE-bpKQ", text);
    }

    /// <summary>
    /// X11DragDrop takes its events out of Avalonia's X11 dispatch, which is internal: an Avalonia
    /// update that changes it must fail here, not silently turn drag and drop off on Linux.
    /// </summary>
    [Fact]
    public void Avalonias_x11_event_dispatch_is_where_the_drop_target_hooks_in()
    {
        var points = X11DragDrop.HookPoints();
        Assert.NotNull(points);
        Assert.Equal("EventHandler", points.Value.Handler.Name);
    }
}

public class Prompt9StreamViewModelTests
{
    private static void Starting(FakePlayer p, bool starting)
    {
        p.State.IsStartingStream = starting;
        p.RaiseState("stream-starting");
    }

    [Fact]
    public async Task Play_while_a_link_opens_keeps_it_playing()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync("https://example.com/concert.mp4");
        Prompt8ViewModelTests.Play(r.Player);
        Starting(r.Player, true);
        Assert.True(r.Vm.IsStartingStream);
        Assert.False(r.Vm.ShowPauseIcon);
        Assert.False(r.Vm.ShowPlayIcon);
        Assert.True(r.Vm.ShowsBufferingText);

        r.Vm.TogglePauseCommand.Execute(null);
        r.Vm.HandleKey("SPACE");
        Assert.DoesNotContain("TogglePause", r.Player.Calls);
        Assert.Equal(2, r.Player.Calls.Count(c => c == "SetPaused False"));

        // Once it plays, Play / Pause is itself again.
        Starting(r.Player, false);
        Assert.True(r.Vm.ShowPauseIcon);
        r.Vm.TogglePauseCommand.Execute(null);
        Assert.Contains("TogglePause", r.Player.Calls);
    }

    [Fact]
    public async Task The_bars_stay_up_while_a_link_opens()
    {
        using var r = new Rig();
        await r.Vm.OpenInputAsync("https://example.com/concert.mp4");
        Prompt8ViewModelTests.Play(r.Player);
        Starting(r.Player, true);
        r.Vm.TickAutoHide(DateTime.UtcNow.AddSeconds(30));
        Assert.True(r.Vm.ControlsVisible);
        Assert.NotEqual("", r.Vm.BufferingText);
        Starting(r.Player, false);
        r.Vm.TickAutoHide(DateTime.UtcNow.AddSeconds(30));
        Assert.False(r.Vm.ControlsVisible);
    }

    [AvaloniaFact]
    public void The_bars_say_a_link_is_opening()
    {
        using var r = new Rig();
        r.Vm.IsIdle = false;
        var top = new TopBar { DataContext = r.Vm, Width = 1280 };
        var bottom = new ControlsBar { DataContext = r.Vm, Width = 1280 };
        top.Show();
        bottom.Show();
        Assert.False(top.FindControl<TextBlock>("OpeningText")!.IsVisible);
        r.Vm.IsStartingStream = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(top.FindControl<TextBlock>("OpeningText")!.IsVisible);
        Assert.True(bottom.FindControl<PathIcon>("OpeningSpinner")!.IsVisible);
        Assert.StartsWith("Opening", ToolTip.GetTip(bottom.FindControl<Button>("PlayButton")!) as string);
        Assert.NotNull(bottom.CaptureRenderedFrame());
        top.Close();
        bottom.Close();
    }

    [Fact]
    public void The_log_says_when_the_pointer_hid_and_what_showed_it()
    {
        using var r = new Rig();
        Assert.Contains("Pointer: shown (windowed); last hidden never; last shown never", r.Vm.BuildLog());
        r.Vm.IsFullscreen = true;
        r.Vm.IsIdle = false;
        r.Vm.TickAutoHide(DateTime.UtcNow.AddSeconds(10));
        Assert.True(r.Vm.IsCursorHidden);
        r.Vm.ReportPointer(10, 10, true);
        r.Vm.ReportPointer(20, 10, true);
        Assert.False(r.Vm.IsCursorHidden);
        Assert.Contains(", by the OS pointer position", r.Vm.BuildLog());
    }
}

public class Prompt9RipDialogTests
{
    [Fact]
    public void A_video_ts_folder_is_ripped_to_mkv_only()
    {
        using var r = new Rig();
        var vm = RipDialogTests.Dialog(r, RipDialogTests.Dvd);
        Assert.True(vm.ShowDecrypt);
        var folder = Directory.CreateDirectory(Path.Combine(r.Root, "CONCERT", "VIDEO_TS")).FullName;
        vm.DvdModeIndex = 1;
        vm.UseDvdSource(folder);
        Assert.True(vm.IsFolderSource);
        Assert.False(vm.ShowDecrypt);
        Assert.True(vm.IsMkv);
        Assert.Equal(DvdRipMode.Mkv, vm.DvdOptions()!.Mode);
        // An image is a disc's contents still encrypted: both again.
        vm.UseDvdSource(Path.Combine(r.Root, "Concert.iso"));
        Assert.False(vm.IsFolderSource);
        Assert.True(vm.ShowDecrypt);
    }

    [Fact]
    public void The_mkvs_video_is_copied_or_encoded_as_chosen()
    {
        using var r = new Rig();
        var vm = RipDialogTests.Dialog(r, RipDialogTests.Dvd);
        Assert.Equal(DvdVideo.Copy, vm.DvdOptions()!.Video);
        Assert.Contains("MPEG-2 video as it is", vm.MkvNote);
        vm.DvdVideoIndex = 1;
        Assert.Equal(DvdVideo.X264, vm.DvdOptions()!.Video);
        Assert.Contains("interlaced video stays interlaced", vm.MkvNote);
        vm.DvdVideoIndex = 2;
        Assert.Equal(DvdVideo.X265, vm.DvdOptions()!.Video);
        Assert.Contains("50 frames a second", vm.MkvNote);
        Assert.True(vm.StartCommand.CanExecute(null));
        // The VIDEO_TS copy has no video to encode.
        vm.DvdModeIndex = 1;
        Assert.Equal(DvdVideo.Copy, vm.DvdOptions()!.Video);
    }

    [Fact]
    public void An_ffmpeg_without_an_encoder_doesnt_offer_it()
    {
        using var r = new Rig();
        var vm = RipDialogTests.Dialog(r, RipDialogTests.Dvd, encoders: new HashSet<string> { "libx264" });
        Assert.True(vm.HasX264);
        Assert.False(vm.HasX265);
        vm.DvdVideoIndex = 2;
        Assert.False(vm.StartCommand.CanExecute(null));
        vm.DvdVideoIndex = 1;
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void An_encodes_progress_shows_its_speed() =>
        Assert.Equal("Encoding title 2 (x265) · 42% · 1.3×", MainViewModel.DescribeRip(new RipProgress("Encoding title 2 (x265)", 0, 0, 0.42, 1.3)));

    [Theory]
    [InlineData(1366, 728, 1.0, 720, 688)] // a laptop: 768 less the taskbar
    [InlineData(1920, 1040, 1.5, 720, 653.333)] // 1080p at 150%
    [InlineData(2560, 1400, 1.0, 720, 900)] // room to spare: its own maximum
    [InlineData(1280, 680, 2.0, 640, 300)] // narrower than the window
    public void The_window_is_no_bigger_than_its_screen(int width, int height, double scaling, double expectedWidth, double expectedHeight)
    {
        var fit = RipDialogLayout.Fit(new PixelRect(0, 0, width, height), scaling, 720);
        Assert.Equal(expectedWidth, fit.Width, 2);
        Assert.Equal(expectedHeight, fit.Height, 2);
    }

    [Fact]
    public void A_window_hanging_off_the_screen_is_moved_back_on()
    {
        var area = new PixelRect(0, 0, 1366, 728);
        Assert.Equal(new PixelPoint(323, 40), RipDialogLayout.KeepInside(new PixelPoint(323, -120), new PixelSize(720, 688), area, 40));
        Assert.Equal(new PixelPoint(646, 100), RipDialogLayout.KeepInside(new PixelPoint(900, 100), new PixelSize(720, 400), area, 0));
        // Already inside: where it is.
        Assert.Equal(new PixelPoint(100, 100), RipDialogLayout.KeepInside(new PixelPoint(100, 100), new PixelSize(720, 400), area, 40));
    }

    [AvaloniaFact]
    public void On_a_short_screen_the_form_scrolls_and_the_rip_button_stays_in_view()
    {
        using var r = new Rig();
        var vm = RipDialogTests.Dialog(r, RipDialogTests.Dvd);
        var w = new RipDialog { DataContext = vm };
        w.Show();
        w.MaxHeight = 360;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var form = w.FindControl<ScrollViewer>("Form")!;
        var start = w.FindControl<Button>("StartButton")!;
        Assert.DoesNotContain(form, start.GetVisualAncestors());
        Assert.True(w.ClientSize.Height <= 360);
        var bottom = start.TranslatePoint(new Point(0, start.Bounds.Height), w)!.Value.Y;
        Assert.True(bottom <= w.ClientSize.Height, $"the Rip button ends at {bottom}, below the window's {w.ClientSize.Height}");
        Assert.True(form.Extent.Height > form.Viewport.Height);
        Assert.True(w.FindControl<ComboBox>("VideoCodec")!.IsVisible);
        Assert.NotNull(w.CaptureRenderedFrame());
        w.Close();
    }

    [AvaloniaFact]
    public void A_folder_shows_no_decrypted_copy_choice()
    {
        using var r = new Rig();
        var vm = RipDialogTests.Dialog(r, RipDialogTests.Dvd);
        var w = new RipDialog { DataContext = vm };
        w.Show();
        Assert.True(w.FindControl<RadioButton>("DecryptMode")!.IsVisible);
        vm.UseDvdSource(Directory.CreateDirectory(Path.Combine(r.Root, "VIDEO_TS")).FullName);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(w.FindControl<RadioButton>("DecryptMode")!.IsVisible);
        Assert.True(w.FindControl<RadioButton>("MkvMode")!.IsChecked);
        w.Close();
    }
}

/// <summary>
/// Real Windows windows, checked on CI's Windows runner (in a step of its own, which may fail
/// without failing the run: a runner's session may not show a pointer at all). The engines' video
/// is a child window made by another thread, as here.
/// </summary>
[Trait("Category", "desktop")]
[SupportedOSPlatform("windows")] // each test returns at once elsewhere
public class Prompt9WindowsDesktopTests
{
    [Fact]
    public void The_pointer_hides_over_a_child_window_of_another_thread()
    {
        if (!OperatingSystem.IsWindows()) return;
        Win32Rig.Run(rig =>
        {
            Win32Rig.SetCursorPos(rig.Center.X, rig.Center.Y);
            rig.Pump();
            Assert.True(Win32Rig.PointerShowing(), "inconclusive: this session shows no pointer to begin with");
            Win32Pointer.SetHidden(true);
            rig.Pump();
            try { Assert.False(Win32Rig.PointerShowing(), "the pointer still shows over the other thread's window"); }
            finally { Win32Pointer.SetHidden(false); }
            rig.Pump();
            Assert.True(Win32Rig.PointerShowing(), "the pointer didn't come back");
        });
    }

    [Fact]
    public void A_drop_target_registered_by_another_thread_is_revoked()
    {
        if (!OperatingSystem.IsWindows()) return;
        Win32Rig.Run(rig =>
        {
            Assert.NotEqual(IntPtr.Zero, Win32Rig.GetPropW(rig.Child, Win32DropTargets.DropTargetProperty));
            Assert.Equal(1, Win32DropTargets.RevokeInside(rig.Top));
            Assert.Equal(IntPtr.Zero, Win32Rig.GetPropW(rig.Child, Win32DropTargets.DropTargetProperty));
            Assert.Equal(0, Win32DropTargets.RevokeInside(rig.Top));
        });
    }
}

/// <summary>A top-level window, and inside it a child window made, and registered as a drop target, by a second thread.</summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32Rig
{
    private const uint WsPopup = 0x80000000, WsChild = 0x40000000, WsVisible = 0x10000000;

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropTarget
    {
        [PreserveSig] int DragEnter(IntPtr data, int keys, long point, ref int effect);
        [PreserveSig] int DragOver(int keys, long point, ref int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(IntPtr data, int keys, long point, ref int effect);
    }

    [ComVisible(true)]
    private sealed class Refuses : IDropTarget
    {
        public int DragEnter(IntPtr data, int keys, long point, ref int effect) { effect = 0; return 0; }
        public int DragOver(int keys, long point, ref int effect) { effect = 0; return 0; }
        public int DragLeave() => 0;
        public int Drop(IntPtr data, int keys, long point, ref int effect) { effect = 0; return 0; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size, Flags;
        public IntPtr Cursor;
        public int X, Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WndClassEx c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string cls, string? name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Msg m, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg m);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetPropW(IntPtr window, string name);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(IntPtr window, IDropTarget target);

    public IntPtr Top { get; private set; }
    public IntPtr Child { get; private set; }
    public (int X, int Y) Center => (300, 250);

    public static bool PointerShowing()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && (info.Flags & 1) != 0;
    }

    /// <summary>Lets both threads handle what came in (the pointer's position, WM_SETCURSOR).</summary>
    public void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            PumpOnce();
            Thread.Sleep(10);
        }
    }

    private static void PumpOnce()
    {
        while (PeekMessageW(out var m, IntPtr.Zero, 0, 0, 1))
        {
            TranslateMessage(ref m);
            DispatchMessageW(ref m);
        }
    }

    private static string RegisterClass()
    {
        var name = "ParamajaTest" + Guid.NewGuid().ToString("N");
        var c = new WndClassEx
        {
            Size = Marshal.SizeOf<WndClassEx>(),
            WndProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW"),
            Instance = GetModuleHandleW(null),
            Cursor = LoadCursorW(IntPtr.Zero, 32512), // the arrow
            ClassName = name,
        };
        Assert.NotEqual(0, RegisterClassExW(ref c));
        return name;
    }

    /// <summary>Runs <paramref name="test"/> on an STA thread owning the top-level window, with the child's thread pumping.</summary>
    public static void Run(Action<Win32Rig> test)
    {
        var rig = new Win32Rig();
        Exception? failed = null;
        var main = new Thread(() =>
        {
            var stop = new ManualResetEventSlim();
            var childReady = new ManualResetEventSlim();
            Thread? childThread = null;
            try
            {
                OleInitialize(IntPtr.Zero);
                var cls = RegisterClass();
                rig.Top = CreateWindowExW(0, cls, "Paramaja test", WsPopup | WsVisible, 100, 100, 400, 300, IntPtr.Zero, IntPtr.Zero,
                    GetModuleHandleW(null), IntPtr.Zero);
                Assert.NotEqual(IntPtr.Zero, rig.Top);
                childThread = new Thread(() =>
                {
                    OleInitialize(IntPtr.Zero);
                    rig.Child = CreateWindowExW(0, cls, null, WsChild | WsVisible, 0, 0, 400, 300, rig.Top, IntPtr.Zero,
                        GetModuleHandleW(null), IntPtr.Zero);
                    RegisterDragDrop(rig.Child, new Refuses());
                    childReady.Set();
                    while (!stop.IsSet)
                    {
                        PumpOnce();
                        Thread.Sleep(5);
                    }
                    DestroyWindow(rig.Child);
                });
                childThread.SetApartmentState(ApartmentState.STA);
                childThread.Start();
                // The child's thread makes its window inside ours: we pump meanwhile (it waits on us).
                for (var i = 0; i < 500 && !childReady.IsSet; i++) { PumpOnce(); Thread.Sleep(5); }
                Assert.True(childReady.IsSet, "the child window wasn't made");
                Assert.NotEqual(IntPtr.Zero, rig.Child);
                rig.Pump();
                test(rig);
            }
            catch (Exception e) { failed = e; }
            finally
            {
                stop.Set();
                for (var i = 0; i < 200 && childThread is { IsAlive: true }; i++) { PumpOnce(); Thread.Sleep(5); }
                if (rig.Top != IntPtr.Zero) DestroyWindow(rig.Top);
            }
        });
        main.SetApartmentState(ApartmentState.STA);
        main.Start();
        main.Join();
        if (failed is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed).Throw();
    }
}
