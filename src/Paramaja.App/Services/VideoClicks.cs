using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace Paramaja.App.Services;

/// <summary>
/// Left clicks on the video, for double-click fullscreen. The video is a native window of mpv's
/// (or libVLC's, for discs), and its clicks don't reliably come back to the app: on Windows mpv's
/// window doesn't pass them on, on Linux they didn't make it either, and libVLC never reports
/// them. So they're read from the OS: a low-level mouse hook on Windows, XInput2's raw button
/// events on Linux (X11, or XWayland on a Wayland session). Only clicks that land in the player
/// window count (the video included), not those on the control bars, their menus or other apps.
/// </summary>
public static class VideoClicks
{
    /// <summary>
    /// Calls <paramref name="clicked"/> on the UI thread; dispose to stop. Null where clicks
    /// can't be read this way (the engines' own reports are used then).
    /// </summary>
    public static IDisposable? Watch(IntPtr playerWindow, Action clicked)
    {
        if (OperatingSystem.IsWindows()) return new Win32ClickHook(playerWindow, clicked);
        if (OperatingSystem.IsLinux()) return X11ClickWatch.Start(playerWindow, clicked);
        return null;
    }
}

[SupportedOSPlatform("windows")]
internal sealed partial class Win32ClickHook : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x0201;
    private const uint GaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public Point Pt;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    private static partial IntPtr SetWindowsHookEx(int idHook, IntPtr proc, IntPtr module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hook);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(Point point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr window, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string? name);

    private readonly IntPtr _playerWindow;
    private readonly Action _clicked;
    private readonly HookProc _proc; // kept alive: the OS holds only a raw pointer to it
    private IntPtr _hook;

    public Win32ClickHook(IntPtr playerWindow, Action clicked)
    {
        _playerWindow = playerWindow;
        _clicked = clicked;
        _proc = OnMouse;
        // Runs on the calling (UI) thread's message loop.
        _hook = SetWindowsHookEx(WhMouseLl, Marshal.GetFunctionPointerForDelegate(_proc), GetModuleHandle(null), 0);
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == WmLButtonDown)
        {
            var info = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            // The window under the pointer belongs to the player (mpv's video is a child of it).
            if (GetAncestor(WindowFromPoint(info.Pt), GaRoot) == _playerWindow) _clicked();
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}

/// <summary>
/// Every left button press on the X server, from XInput2's raw events (selected on the root
/// window, which takes nothing from anyone else's events), on a connection and thread of its own.
/// A press counts when the windows under the pointer, from the root down, include the player's.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class X11ClickWatch : IDisposable
{
    private const string X11 = "libX11.so.6";
    private const string Xi = "libXi.so.6";
    private const int GenericEvent = 35;
    private const int XiRawButtonPress = 15;
    private const int XiAllMasterDevices = 1;
    private const short PollIn = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct XiEventMask
    {
        public int DeviceId;
        public int MaskLength;
        public IntPtr Mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport(X11)] private static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport(X11)] private static partial int XCloseDisplay(IntPtr display);
    [LibraryImport(X11)] private static partial IntPtr XDefaultRootWindow(IntPtr display);
    [LibraryImport(X11)] private static partial int XConnectionNumber(IntPtr display);
    [LibraryImport(X11)] private static partial int XPending(IntPtr display);
    [LibraryImport(X11)] private static partial int XNextEvent(IntPtr display, IntPtr eventReturn);
    [LibraryImport(X11)] private static partial int XFlush(IntPtr display);
    [LibraryImport(X11)] private static partial int XGetEventData(IntPtr display, IntPtr cookie);
    [LibraryImport(X11)] private static partial void XFreeEventData(IntPtr display, IntPtr cookie);

    [LibraryImport(X11, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int XQueryExtension(IntPtr display, string name, out int majorOpcode, out int firstEvent, out int firstError);

    [LibraryImport(X11)]
    private static partial int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [LibraryImport(Xi)] private static partial int XIQueryVersion(IntPtr display, ref int major, ref int minor);
    [LibraryImport(Xi)] private static partial int XISelectEvents(IntPtr display, IntPtr window, ref XiEventMask masks, int count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int poll(ref PollFd fds, nuint count, int timeout);

    // XGenericEventCookie and XIRawEvent on 64-bit Linux: the fields read here.
    private const int CookieExtension = 32, CookieEvType = 36, CookieData = 48;
    private const int RawDetail = 56;

    private readonly IntPtr _display;
    private readonly IntPtr _root;
    private readonly IntPtr _player;
    private readonly int _xiOpcode;
    private readonly Action _clicked;
    private readonly Thread _thread;
    private volatile bool _stop;

    private X11ClickWatch(IntPtr display, int xiOpcode, IntPtr player, Action clicked)
    {
        _display = display;
        _root = XDefaultRootWindow(display);
        _xiOpcode = xiOpcode;
        _player = player;
        _clicked = clicked;
        _thread = new Thread(Run) { IsBackground = true, Name = "Paramaja X11 clicks" };
    }

    /// <summary>Null without an X server, XInput 2.0 or libXi: the engines' own click reports count then.</summary>
    public static X11ClickWatch? Start(IntPtr playerWindow, Action clicked)
    {
        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return null;
            int major = 2, minor = 2; // 2.1 and up get raw events whatever grabs are active
            if (XQueryExtension(display, "XInputExtension", out var opcode, out _, out _) == 0
                || XIQueryVersion(display, ref major, ref minor) != 0 || major < 2)
            {
                XCloseDisplay(display);
                return null;
            }
            var mask = new byte[4];
            mask[XiRawButtonPress >> 3] |= (byte)(1 << (XiRawButtonPress & 7));
            var handle = GCHandle.Alloc(mask, GCHandleType.Pinned);
            try
            {
                var masks = new XiEventMask { DeviceId = XiAllMasterDevices, MaskLength = mask.Length, Mask = handle.AddrOfPinnedObject() };
                XISelectEvents(display, XDefaultRootWindow(display), ref masks, 1);
                XFlush(display);
            }
            finally
            {
                handle.Free();
            }
            var watch = new X11ClickWatch(display, opcode, playerWindow, clicked);
            watch._thread.Start();
            return watch;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private void Run()
    {
        var ev = Marshal.AllocHGlobal(256); // an XEvent is 192 bytes
        try
        {
            var fd = new PollFd { Fd = XConnectionNumber(_display), Events = PollIn };
            while (!_stop)
            {
                // Wakes up now and then to notice Dispose.
                if (XPending(_display) == 0 && poll(ref fd, 1, 250) <= 0) continue;
                while (!_stop && XPending(_display) > 0)
                {
                    XNextEvent(_display, ev);
                    if (IsLeftPress(ev) && PointerIsOverPlayer()) Dispatcher.UIThread.Post(_clicked);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ev);
            XCloseDisplay(_display);
        }
    }

    private bool IsLeftPress(IntPtr ev)
    {
        if (Marshal.ReadInt32(ev) != GenericEvent || Marshal.ReadInt32(ev, CookieExtension) != _xiOpcode
            || Marshal.ReadInt32(ev, CookieEvType) != XiRawButtonPress) return false;
        if (XGetEventData(_display, ev) == 0) return false;
        try
        {
            var data = Marshal.ReadIntPtr(ev, CookieData);
            return data != IntPtr.Zero && Marshal.ReadInt32(data, RawDetail) == 1;
        }
        finally
        {
            XFreeEventData(_display, ev);
        }
    }

    /// <summary>The windows under the pointer, from the root down (window manager frames included), include the player's.</summary>
    private bool PointerIsOverPlayer()
    {
        var window = _root;
        for (var depth = 0; depth < 32; depth++)
        {
            if (XQueryPointer(_display, window, out _, out var child, out _, out _, out _, out _, out _) == 0 || child == IntPtr.Zero)
                return false;
            if (child == _player) return true;
            window = child;
        }
        return false;
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
