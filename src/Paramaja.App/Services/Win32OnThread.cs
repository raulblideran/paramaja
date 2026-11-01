using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paramaja.App.Services;

/// <summary>
/// Runs code on the thread that owns a window: mpv and libVLC make their video windows on threads
/// of their own, and some of Windows' calls only work there (RevokeDragDrop; ShowCursor counts per
/// thread's input). A message hook on that thread runs the queued code as the thread next reads a
/// message, and a message posted to the window makes sure there is one. mpv hooks the player's
/// window the same way to follow its size.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class Win32OnThread
{
    private const int WhGetMessage = 3;
    private const uint WmNull = 0;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    private delegate int EnumProc(IntPtr window, IntPtr data);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint process);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    private static partial IntPtr SetWindowsHookEx(int idHook, IntPtr proc, IntPtr module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hook);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial int EnumChildWindows(IntPtr parent, IntPtr proc, IntPtr data);

    private static readonly List<IntPtr> Children = [];
    // Kept alive: Windows only holds raw pointers to them.
    private static readonly HookProc OnMessage = Work;
    private static readonly EnumProc Visit = (window, _) => { Children.Add(window); return 1; };
    private static readonly object Gate = new();
    private static readonly Dictionary<uint, (IntPtr Hook, List<Action> Queue)> Threads = [];

    public static uint ThreadOf(IntPtr window) => GetWindowThreadProcessId(window, out _);

    public static bool IsCurrent(uint thread) => thread == GetCurrentThreadId();

    /// <summary>Every window inside <paramref name="parent"/> (all its descendants). UI thread only.</summary>
    public static List<IntPtr> ChildrenOf(IntPtr parent)
    {
        Children.Clear();
        if (parent != IntPtr.Zero) EnumChildWindows(parent, Marshal.GetFunctionPointerForDelegate(Visit), IntPtr.Zero);
        return [.. Children];
    }

    /// <summary>Runs <paramref name="action"/> on <paramref name="window"/>'s thread: now if that's this one, else soon.</summary>
    public static void Run(IntPtr window, Action action)
    {
        var thread = ThreadOf(window);
        if (thread == 0) return; // gone
        if (IsCurrent(thread))
        {
            action();
            return;
        }
        lock (Gate)
        {
            if (!Threads.TryGetValue(thread, out var t))
                Threads[thread] = t = (SetWindowsHookEx(WhGetMessage, Marshal.GetFunctionPointerForDelegate(OnMessage), IntPtr.Zero, thread), []);
            t.Queue.Add(action);
        }
        PostMessage(window, WmNull, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Removes the hooks (what's still queued is dropped).</summary>
    public static void Unhook()
    {
        lock (Gate)
        {
            foreach (var (hook, _) in Threads.Values) UnhookWindowsHookEx(hook);
            Threads.Clear();
        }
    }

    private static IntPtr Work(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            Action[] work = [];
            lock (Gate)
            {
                if (Threads.TryGetValue(GetCurrentThreadId(), out var t) && t.Queue.Count > 0)
                {
                    work = [.. t.Queue];
                    t.Queue.Clear();
                }
            }
            foreach (var action in work) action();
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }
}
