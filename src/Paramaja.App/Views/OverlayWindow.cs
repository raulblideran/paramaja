using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Paramaja.App.Services;
using Paramaja.App.ViewModels;

namespace Paramaja.App.Views;

/// <summary>
/// Borderless, transparent window floated over the video. The video is a native surface,
/// so regular Avalonia controls can't be drawn on top of it — a separate owned window can.
/// </summary>
public class OverlayWindow : Window
{
    public OverlayWindow()
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SizeToContent = SizeToContent.Manual;

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Source is TextBox) return;
            if (DataContext is MainViewModel vm && KeyMapping.ToCanonical(e) is { } k && vm.HandleKey(k)) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        PointerEntered += (_, _) => { if (DataContext is MainViewModel vm) { vm.IsPointerOverControls = true; vm.NotifyActivity(); } };
        PointerExited += (_, _) => { if (DataContext is MainViewModel vm) vm.IsPointerOverControls = false; };
        PointerMoved += (_, _) => (DataContext as MainViewModel)?.NotifyActivity();
        if (TryGetPlatformHandle() is { HandleDescriptor: "XID" } x11) X11WindowType.MakeDock(x11.Handle);
    }
}
