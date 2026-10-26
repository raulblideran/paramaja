using System.Runtime.InteropServices;

namespace Paramaja.App.Services;

/// <summary>
/// Marks the control bars as dock windows on Linux. A window manager keeps normal windows
/// out of the space its panels reserve (KDE's taskbar, for one), even while the video is
/// fullscreen over that panel, so a normal bottom bar ends up floating above the screen's
/// bottom edge. Docks may go anywhere, are still clickable, and never take keyboard focus.
/// </summary>
public static partial class X11WindowType
{
    private const string X11 = "libX11.so.6";
    private const int PropModeReplace = 0;
    private static readonly IntPtr XaAtom = 4;

    [LibraryImport(X11)] private static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport(X11, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [LibraryImport(X11)]
    private static partial int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format,
        int mode, ref IntPtr data, int elements);
    [LibraryImport(X11)] private static partial int XFlush(IntPtr display);

    private static IntPtr _display;

    /// <summary>Must run before the window is first shown: window managers read the type when they map it.</summary>
    public static void MakeDock(IntPtr window)
    {
        if (!OperatingSystem.IsLinux() || window == IntPtr.Zero) return;
        try
        {
            if (_display == IntPtr.Zero) _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero) return;
            var dock = XInternAtom(_display, "_NET_WM_WINDOW_TYPE_DOCK", false);
            XChangeProperty(_display, window, XInternAtom(_display, "_NET_WM_WINDOW_TYPE", false), XaAtom, 32,
                PropModeReplace, ref dock, 1);
            XFlush(_display);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }
}
