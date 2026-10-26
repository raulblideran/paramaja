using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paramaja.App.Services;

/// <summary>
/// Left clicks on the video, for double-click fullscreen. On Windows mpv's video window
/// doesn't reliably pass mouse events back (see <see cref="CursorProbe"/>), so clicks are
/// read from the OS instead: a low-level mouse hook, counting only clicks that land in the
/// player window (mpv's video included), not on the control bars or other apps. On Linux
/// mpv reports the clicks itself (PlayerService binds MBTN_LEFT).
/// </summary>
public static class VideoClicks
{
    /// <summary>Calls <paramref name="clicked"/> on the UI thread; dispose to stop.</summary>
    public static IDisposable Watch(IntPtr playerWindow, Action clicked) =>
        OperatingSystem.IsWindows() ? new Win32ClickHook(playerWindow, clicked) : new Nothing();

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
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
