using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Paramaja.App.Services;
using Paramaja.App.ViewModels;

namespace Paramaja.App.Views;

public partial class MainWindow : Window
{
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".sub", ".vtt", ".sup", ".idx"];

    private ControlsBar? _bottom;
    private TopBar? _top;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _ticks;
    private readonly ICursorProbe _cursor = CursorProbe.Create();
    private readonly IDisplayHdrProbe _hdr = DisplayHdr.Create();
    private PixelRect? _hdrScreen;
    private IDisposable? _clicks;

    private MainViewModel Vm => (MainViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
        Video.HandleCreated += h => Vm.AttachPlayer(h);
        DiscVideo.HandleCreated += h => Vm.AttachDiscSurface(h);

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, OnDrop);
        PointerMoved += (_, _) => Vm.NotifyActivity();
        PointerWheelChanged += (_, e) => Vm.HandleKey(e.Delta.Y > 0 ? "WHEEL_UP" : "WHEEL_DOWN");

        PositionChanged += (_, _) =>
        {
            UpdateOverlays();
            // Moving to another monitor may move between HDR and SDR.
            if (Screens.ScreenFromWindow(this)?.Bounds != _hdrScreen) ProbeHdr();
        };
        Resized += (_, _) => UpdateOverlays();
        Video.LayoutUpdated += (_, _) => UpdateOverlays();
        DiscVideo.LayoutUpdated += (_, _) => UpdateOverlays();

        _tick.Tick += (_, _) =>
        {
            if (_cursor.Position() is { } p)
            {
                var pointer = new PixelPoint(p.X, p.Y);
                var inside = OverlayPlacement.IsPointerOverPlayer(pointer,
                    OverlayPlacement.WindowRect(Position, ClientSize, RenderScaling),
                    Screens.ScreenFromWindow(this)?.Bounds,
                    WindowState == WindowState.FullScreen);
                Vm.ReportPointer(p.X, p.Y, inside);
            }
            Vm.TickAutoHide(DateTime.UtcNow);
            // Cheap when nothing changed, and heals anything an event missed (e.g. a
            // fullscreen switch that moved the video after the last layout pass).
            UpdateOverlays();
            if (++_ticks % 4 == 0)
            {
                if (Vm.IsTorrent) Vm.RefreshTorrentStats();
                Vm.RefreshHealth();
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainViewModel vm) return;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.FullscreenRequested += SetFullscreen;
        ApplySurfaces();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _bottom = new ControlsBar { DataContext = DataContext };
        _top = new TopBar { DataContext = DataContext };
        // On Windows the bars are shown once and parked off-screen while hidden; see
        // OverlayPlacement.Parked.
        foreach (var bar in new Window[] { _top, _bottom })
        {
            if (OverlayPlacement.ParkInsteadOfHiding)
            {
                bar.Position = OverlayPlacement.Parked;
                bar.Show(this);
            }
            // Clicking a bar activates it and deactivates us; judge once that has settled.
            bar.Activated += (_, _) => Dispatcher.UIThread.Post(UpdateOverlays);
            bar.Deactivated += (_, _) => Dispatcher.UIThread.Post(UpdateOverlays);
        }
        Activated += (_, _) =>
        {
            Dispatcher.UIThread.Post(UpdateOverlays);
            ProbeHdr(); // catches HDR switched on or off in the system settings meanwhile
        };
        Deactivated += (_, _) => Dispatcher.UIThread.Post(UpdateOverlays);
        _tick.Start();
        UpdateOverlays();
        ProbeHdr();
        // Double-click fullscreen; on Linux mpv reports the clicks instead (PlayerService).
        if (TryGetPlatformHandle()?.Handle is { } hwnd)
            _clicks = VideoClicks.Watch(hwnd, () => { if (!Vm.IsIdle) Vm.HandleClick(DateTime.UtcNow); });
    }

    private void ProbeHdr()
    {
        if (TryGetPlatformHandle()?.Handle is not { } hwnd || DataContext is not MainViewModel vm) return;
        _hdrScreen = Screens.ScreenFromWindow(this)?.Bounds;
        vm.DisplayIsHdr = _hdr.IsHdrActive(hwnd);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _tick.Stop();
        _clicks?.Dispose();
        _bottom?.Close();
        _top?.Close();
        // A resize or move already queued would otherwise try to show the closed bars again.
        _bottom = null;
        _top = null;
        base.OnClosing(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            if (DataContext is MainViewModel vm) vm.IsFullscreen = WindowState == WindowState.FullScreen;
            UpdateOverlays();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsIdle) or nameof(MainViewModel.IsDiscMode):
                ApplySurfaces();
                UpdateOverlays();
                break;
            case nameof(MainViewModel.ControlsVisible):
                UpdateOverlays();
                Cursor = Vm.ControlsVisible ? Cursor.Default : new Cursor(StandardCursorType.None);
                break;
        }
    }

    /// <summary>The surface of the engine that's playing: libVLC's for discs, mpv's otherwise.</summary>
    private Controls.VideoHost ActiveVideo => Vm.IsDiscMode ? DiscVideo : Video;

    /// <summary>
    /// Only the playing engine's surface fills the window; the other, and both while idle, are
    /// shrunk to nothing so the welcome screen (regular Avalonia content, which can't draw over a
    /// native window) shows. The surfaces themselves stay alive.
    /// </summary>
    private void ApplySurfaces()
    {
        foreach (var surface in new[] { Video, DiscVideo })
        {
            var shown = !Vm.IsIdle && surface == ActiveVideo;
            surface.Width = shown ? double.NaN : 1;
            surface.Height = shown ? double.NaN : 1;
            surface.HorizontalAlignment = shown ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Left;
            surface.VerticalAlignment = shown ? Avalonia.Layout.VerticalAlignment.Stretch : Avalonia.Layout.VerticalAlignment.Top;
        }
    }

    private void UpdateOverlays()
    {
        if (_bottom is null || _top is null || DataContext is not MainViewModel vm) return;
        var fullscreen = WindowState == WindowState.FullScreen;
        var appActive = IsActive || _top.IsActive || _bottom.IsActive;
        var visible = vm.ControlsVisible && !vm.IsIdle && WindowState != WindowState.Minimized && IsVisible
                      && ActiveVideo.Bounds.Width > 100
                      && (!OverlayPlacement.HideWhileInactive || appActive || vm.IsPointerOverControls);

        var topmost = OverlayPlacement.ShouldBeTopmost(fullscreen, appActive);
        if (_top.Topmost != topmost) _top.Topmost = topmost;
        if (_bottom.Topmost != topmost) _bottom.Topmost = topmost;

        var area = visible
            ? OverlayPlacement.PlayerArea(ActiveVideo.PointToScreen(new Point(0, 0)), ActiveVideo.Bounds.Size, RenderScaling,
                Screens.ScreenFromWindow(this)?.Bounds, fullscreen)
            : default;
        if (visible)
        {
            _top.Width = _bottom.Width = area.Width / RenderScaling;
            _top.Height = TopBar.BarHeight;
            _bottom.Height = ControlsBar.BarHeight;
        }
        if (!visible && !OverlayPlacement.ParkInsteadOfHiding)
        {
            if (_top.IsVisible) _top.Hide();
            if (_bottom.IsVisible) _bottom.Hide();
            return;
        }
        var (top, bottom) = OverlayPlacement.Place(area, ControlsBar.BarHeight, RenderScaling, visible);
        if (_top.Position != top) _top.Position = top;
        if (_bottom.Position != bottom) _bottom.Position = bottom;
        if (!_top.IsVisible) _top.Show(this);
        if (!_bottom.IsVisible) _bottom.Show(this);
    }

    private void SetFullscreen(bool? on)
    {
        var fs = on ?? WindowState != WindowState.FullScreen;
        if (!fs && WindowState != WindowState.FullScreen) return;
        // Leaving fullscreen goes back to a maximized window, not a small one.
        WindowState = fs ? WindowState.FullScreen : WindowState.Maximized;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        if (KeyMapping.ToCanonical(e) is { } key && Vm.HandleKey(key)) e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var path = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null);
        if (path is not null)
        {
            var isSub = SubtitleExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
            if (isSub && !Vm.IsIdle && Vm.Player is { } p) p.AddSubtitle(path);
            else await Vm.OpenInputAsync(path);
            return;
        }
        if (e.DataTransfer.TryGetText() is { } text) await Vm.OpenInputAsync(text);
    }
}
