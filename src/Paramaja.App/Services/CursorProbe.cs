using System.Runtime.InteropServices;

namespace Paramaja.App.Services;

/// <summary>
/// Reads the pointer's screen position straight from the OS. Over the video the mouse
/// belongs to mpv's native window, and its events don't always make it back to us, so
/// "show the controls when the mouse moves" can't rely on them alone.
/// </summary>
public interface ICursorProbe
{
    (int X, int Y)? Position();
}

public static class CursorProbe
{
    public static ICursorProbe Create() =>
        OperatingSystem.IsWindows() ? new Win32CursorProbe()
        : OperatingSystem.IsLinux() ? new X11CursorProbe()
        : new NoCursorProbe();
}

internal sealed class NoCursorProbe : ICursorProbe
{
    public (int X, int Y)? Position() => null;
}

internal sealed partial class Win32CursorProbe : ICursorProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    public (int X, int Y)? Position() => GetCursorPos(out var p) ? (p.X, p.Y) : null;
}

/// <summary>Avalonia runs on X11 (XWayland on a Wayland session), so XQueryPointer sees our windows.</summary>
internal sealed partial class X11CursorProbe : ICursorProbe
{
    private const string X11 = "libX11.so.6";

    [LibraryImport(X11)] private static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport(X11)] private static partial IntPtr XDefaultRootWindow(IntPtr display);

    [LibraryImport(X11)]
    private static partial int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    private IntPtr _display;
    private bool _failed;

    public (int X, int Y)? Position()
    {
        if (_failed) return null;
        try
        {
            if (_display == IntPtr.Zero) _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero) { _failed = true; return null; }
            return XQueryPointer(_display, XDefaultRootWindow(_display), out _, out _, out var x, out var y, out _, out _, out _) != 0
                ? (x, y)
                : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _failed = true;
            return null;
        }
    }
}
