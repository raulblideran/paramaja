using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NegsPlayer.App.Services;
using NegsPlayer.App.ViewModels;

namespace NegsPlayer.App.Views;

public partial class MainWindow : Window
{
    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".sub", ".vtt", ".sup", ".idx"];

    private ControlsBar? _bottom;
    private TopBar? _top;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _ticks;

    private MainViewModel Vm => (MainViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
        Video.HandleCreated += h => Vm.AttachPlayer(h);

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, OnDrop);
        PointerMoved += (_, _) => Vm.NotifyActivity();
        PointerWheelChanged += (_, e) => Vm.HandleKey(e.Delta.Y > 0 ? "WHEEL_UP" : "WHEEL_DOWN");
        DoubleTapped += (_, _) => { if (!Vm.IsIdle) Vm.ToggleFullscreenCommand.Execute(null); };

        PositionChanged += (_, _) => UpdateOverlays();
        Resized += (_, _) => UpdateOverlays();
        Video.LayoutUpdated += (_, _) => UpdateOverlays();

        _tick.Tick += (_, _) =>
        {
            Vm.TickAutoHide(DateTime.UtcNow);
            if (++_ticks % 4 == 0 && Vm.IsTorrent) Vm.RefreshTorrentStats();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainViewModel vm) return;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.FullscreenRequested += SetFullscreen;
        ApplyIdle(vm.IsIdle);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _bottom = new ControlsBar { DataContext = DataContext };
        _top = new TopBar { DataContext = DataContext };
        _tick.Start();
        UpdateOverlays();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _tick.Stop();
        _bottom?.Close();
        _top?.Close();
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
            case nameof(MainViewModel.IsIdle):
                ApplyIdle(Vm.IsIdle);
                UpdateOverlays();
                break;
            case nameof(MainViewModel.ControlsVisible):
                UpdateOverlays();
                Cursor = Vm.ControlsVisible ? Cursor.Default : new Cursor(StandardCursorType.None);
                break;
        }
    }

    /// <summary>
    /// While idle the native surface is shrunk to nothing so the welcome screen (regular Avalonia
    /// content, which can't draw over a native window) is visible. The surface itself stays alive.
    /// </summary>
    private void ApplyIdle(bool idle)
    {
        Video.Width = idle ? 1 : double.NaN;
        Video.Height = idle ? 1 : double.NaN;
        Video.HorizontalAlignment = idle ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Stretch;
        Video.VerticalAlignment = idle ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Stretch;
    }

    private void UpdateOverlays()
    {
        if (_bottom is null || _top is null || DataContext is not MainViewModel vm) return;
        var show = vm.ControlsVisible && !vm.IsIdle && WindowState != WindowState.Minimized && IsVisible
                   && Video.Bounds.Width > 100;
        if (!show)
        {
            if (_bottom.IsVisible) _bottom.Hide();
            if (_top.IsVisible) _top.Hide();
            return;
        }
        var origin = Video.PointToScreen(new Point(0, 0));
        var scale = RenderScaling;
        var width = Video.Bounds.Width;

        _top.Width = width;
        _top.Height = TopBar.BarHeight;
        _top.Position = origin;

        _bottom.Width = width;
        _bottom.Height = ControlsBar.BarHeight;
        _bottom.Position = new PixelPoint(origin.X, origin.Y + (int)Math.Round((Video.Bounds.Height - ControlsBar.BarHeight) * scale));

        if (!_top.IsVisible) _top.Show(this);
        if (!_bottom.IsVisible) _bottom.Show(this);
    }

    private void SetFullscreen(bool? on)
    {
        var fs = on ?? WindowState != WindowState.FullScreen;
        if (!fs && WindowState != WindowState.FullScreen) return;
        WindowState = fs ? WindowState.FullScreen : WindowState.Normal;
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
