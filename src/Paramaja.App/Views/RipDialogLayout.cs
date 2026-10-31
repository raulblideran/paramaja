using Avalonia;

namespace Paramaja.App.Views;

/// <summary>
/// Keeps the Rip Disc window on its screen: a CD's form is about 950 px tall, more than a laptop's
/// screen (or a 1080p one scaled to 150%) has room for, which hid the Rip button below the screen.
/// The window is made no taller or wider than the screen's usable area; the form scrolls.
/// </summary>
public static class RipDialogLayout
{
    /// <summary>The tallest it's allowed to be anywhere.</summary>
    public const double MaxHeight = 900;

    /// <summary>Room for the title bar the window manager adds above the window, in logical pixels.</summary>
    public const double TitleBar = 40;

    /// <summary>The window's largest size on a screen with this usable area (in pixels) and scaling, in logical pixels.</summary>
    public static Size Fit(PixelRect workingArea, double scaling, double width)
    {
        var s = scaling > 0 ? scaling : 1;
        return new Size(Math.Min(width, workingArea.Width / s), Math.Min(MaxHeight, workingArea.Height / s - TitleBar));
    }

    /// <summary>Where a window of this size (pixels) goes so all of it is inside the usable area: moved as little as it can be.</summary>
    public static PixelPoint KeepInside(PixelPoint position, PixelSize size, PixelRect workingArea, int titleBar)
    {
        var x = Math.Clamp(position.X, workingArea.X, Math.Max(workingArea.X, workingArea.Right - size.Width));
        var y = Math.Clamp(position.Y, workingArea.Y + titleBar, Math.Max(workingArea.Y + titleBar, workingArea.Bottom - size.Height));
        return new PixelPoint(x, y);
    }
}
