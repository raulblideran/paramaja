using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
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
        // A click on a bar (play, the seek bar, the volume) leaves the keyboard with the player
        // window, so Space and the other keys keep working after the bar hides. Not while one of
        // its menus is open: a menu closes when its bar loses the keyboard.
        AddHandler(PointerReleasedEvent, (_, _) => Dispatcher.UIThread.Post(GiveKeyboardBack, DispatcherPriority.Background),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        if (TryGetPlatformHandle() is { HandleDescriptor: "XID" } x11) X11WindowType.MakeDock(x11.Handle);
    }

    private readonly List<FlyoutBase> _menus = [];
    private readonly HashSet<object> _open = [];

    /// <summary>
    /// The bar's menus (button flyouts, drop-downs) report when they open and close: while one
    /// is open the bars stay where they are. A menu is a window of its own, so resting the
    /// pointer on it counted as leaving the bar, which then hid (or moved off screen) and took
    /// the menu to the screen's corner.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_menus.Count > 0) return; // shown again after being hidden: already watched
        foreach (var button in this.GetLogicalDescendants().OfType<Button>())
        {
            if (button.Flyout is not { } flyout) continue;
            _menus.Add(flyout);
            flyout.Opened += (_, _) => MenuOpened(flyout);
            flyout.Closed += (_, _) => MenuClosed(flyout);
        }
        foreach (var box in this.GetLogicalDescendants().OfType<ComboBox>())
        {
            box.DropDownOpened += (_, _) => MenuOpened(box);
            box.DropDownClosed += (_, _) => MenuClosed(box);
        }
    }

    private void MenuOpened(object menu)
    {
        if (_open.Add(menu)) (DataContext as MainViewModel)?.MenuOpened();
    }

    private void MenuClosed(object menu)
    {
        if (!_open.Remove(menu)) return;
        (DataContext as MainViewModel)?.MenuClosed();
        if (_open.Count == 0) Dispatcher.UIThread.Post(GiveKeyboardBack, DispatcherPriority.Background);
    }

    private void GiveKeyboardBack()
    {
        if (_open.Count == 0 && IsActive && Owner is Window owner && owner.IsVisible) owner.Activate();
    }

    /// <summary>Closes the bar's open menus, before it hides: a menu left open would float on its own.</summary>
    public void CloseMenus()
    {
        foreach (var menu in _menus.Where(m => m.IsOpen)) menu.Hide();
    }
}
