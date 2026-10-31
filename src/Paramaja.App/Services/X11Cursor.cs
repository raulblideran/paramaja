using System.Runtime.InteropServices;

namespace Paramaja.App.Services;

/// <summary>
/// Hides the pointer over an X11 window with a blank cursor of its own (Linux; nothing elsewhere).
/// A window without a cursor shows its parent's, so mpv's window inside the video surface goes
/// blank with it; libVLC draws into the surface itself and its own hiding there never worked.
/// </summary>
public static partial class X11Cursor
{
    private const string X11 = "libX11.so.6";

    [StructLayout(LayoutKind.Sequential)]
    private struct XColor
    {
        public nuint Pixel;
        public ushort Red, Green, Blue;
        public byte Flags, Pad;
    }

    [LibraryImport(X11)] private static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport(X11)] private static partial IntPtr XDefaultRootWindow(IntPtr display);
    [LibraryImport(X11)] private static partial IntPtr XCreateBitmapFromData(IntPtr display, IntPtr drawable, byte[] data, uint width, uint height);
    [LibraryImport(X11)]
    private static partial IntPtr XCreatePixmapCursor(IntPtr display, IntPtr source, IntPtr mask, ref XColor foreground,
        ref XColor background, uint x, uint y);
    [LibraryImport(X11)] private static partial int XDefineCursor(IntPtr display, IntPtr window, IntPtr cursor);
    [LibraryImport(X11)] private static partial int XUndefineCursor(IntPtr display, IntPtr window);
    [LibraryImport(X11)] private static partial int XFlush(IntPtr display);

    private static IntPtr _display;
    private static IntPtr _blank;
    private static bool _failed;

    public static void SetHidden(long window, bool hidden)
    {
        if (!OperatingSystem.IsLinux() || window == 0 || _failed) return;
        try
        {
            if (_display == IntPtr.Zero) _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero) { _failed = true; return; }
            if (hidden)
            {
                if (_blank == IntPtr.Zero)
                {
                    var bitmap = XCreateBitmapFromData(_display, XDefaultRootWindow(_display), [0], 1, 1);
                    var black = new XColor();
                    _blank = XCreatePixmapCursor(_display, bitmap, bitmap, ref black, ref black, 0, 0);
                }
                XDefineCursor(_display, (IntPtr)window, _blank);
            }
            else XUndefineCursor(_display, (IntPtr)window);
            XFlush(_display);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _failed = true;
        }
    }
}
