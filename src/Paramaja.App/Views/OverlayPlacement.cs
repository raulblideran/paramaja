using Avalonia;

namespace Paramaja.App.Views;

/// <summary>
/// Where the control bars go, and whether the pointer counts as being over the player.
/// Kept free of windows so it can be tested.
/// </summary>
public static class OverlayPlacement
{
    /// <summary>
    /// Hidden bars are parked here instead of being hidden. Hiding and re-showing an owned
    /// window over a fullscreen one can bring it back behind the video on Windows; a window
    /// that never leaves the screen's z-order can't.
    /// </summary>
    public static readonly PixelPoint Parked = new(-32000, -32000);

    /// <summary>
    /// Windows accepts off-screen positions (it parks minimised windows at -32000 itself).
    /// Linux window managers clamp them back on screen, so there the bars are hidden as usual.
    /// </summary>
    public static bool ParkInsteadOfHiding => OperatingSystem.IsWindows();

    public static (PixelPoint Top, PixelPoint Bottom) Place(
        PixelPoint videoOrigin, double videoHeight, double bottomBarHeight, double scale, bool visible)
    {
        if (!visible) return (Parked, Parked);
        var bottomY = videoOrigin.Y + (int)Math.Round((videoHeight - bottomBarHeight) * scale);
        return (videoOrigin, new PixelPoint(videoOrigin.X, bottomY));
    }

    public static PixelRect WindowRect(PixelPoint position, Size clientSize, double scale) =>
        new(position, PixelSize.FromSize(clientSize, scale));

    /// <summary>
    /// In fullscreen the whole monitor is the player, whatever the window reports as its
    /// own position and size; otherwise it's the window's client area.
    /// </summary>
    public static bool IsPointerOverPlayer(PixelPoint pointer, PixelRect window, PixelRect? screen, bool fullscreen) =>
        fullscreen && screen is { } s ? s.Contains(pointer) : window.Contains(pointer);

    /// <summary>On top of everything only while fullscreen and in front, never over other apps.</summary>
    public static bool ShouldBeTopmost(bool fullscreen, bool appActive) => fullscreen && appActive;
}
