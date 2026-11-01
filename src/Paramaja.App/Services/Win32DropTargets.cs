using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paramaja.App.Services;

/// <summary>
/// Lets files dropped on a playing video reach the player on Windows. mpv registers its video
/// window as a drop target whatever its drag-and-drop option says, then refuses the drop (it's
/// "no": the app opens what's dropped), so a drop over the video went nowhere. With that window's
/// registration revoked, Windows looks up its parents for a drop target and finds the player
/// window's (Avalonia's), whose drop handler opens it.
/// Only the thread that registered a window can revoke it (from any other, RevokeDragDrop fails),
/// and mpv's window is its own thread's: a message hook on that thread does it there, as the
/// thread reads the message posted to the window for it.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class Win32DropTargets
{
    private const int WhGetMessage = 3;
    private const uint WmNull = 0;

    private delegate int EnumProc(IntPtr window, IntPtr data);
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial int EnumChildWindows(IntPtr parent, IntPtr proc, IntPtr data);

    [LibraryImport("user32.dll", EntryPoint = "GetPropW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetProp(IntPtr window, string name);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr window);

    [LibraryImport("ole32.dll")]
    private static partial int RevokeDragDrop(IntPtr window);

    /// <summary>The window property OLE keeps a registered drop target in.</summary>
    internal const string DropTargetProperty = "OleDropTargetInterface";

    // Kept alive: Windows only holds raw pointers to them.
    private static readonly EnumProc Visit = OnWindow;
    private static readonly HookProc OnMessage = OnThreadMessage;

    private static readonly List<IntPtr> Found = [];
    private static readonly object Gate = new();
    /// <summary>Windows waiting for their thread to revoke them, and each such thread's hook.</summary>
    private static readonly HashSet<IntPtr> Waiting = [];
    private static readonly Dictionary<uint, IntPtr> Hooks = [];

    /// <summary>
    /// Revokes the drop targets registered on windows inside <paramref name="host"/> (all its
    /// descendants), and returns how many there were: those of the calling thread at once, others
    /// as their thread next reads a message. mpv makes its window again with each video output, so
    /// the view calls this regularly; a window without a drop target costs a property lookup.
    /// </summary>
    public static int RevokeInside(IntPtr host)
    {
        if (host == IntPtr.Zero) return 0;
        Unhook(finishedOnly: true);
        Found.Clear();
        EnumChildWindows(host, Marshal.GetFunctionPointerForDelegate(Visit), IntPtr.Zero);
        var me = GetCurrentThreadId();
        foreach (var window in Found)
        {
            var thread = GetWindowThreadProcessId(window, out _);
            if (thread == me)
            {
                RevokeDragDrop(window);
                continue;
            }
            lock (Gate)
            {
                if (!Waiting.Add(window)) continue;
                if (!Hooks.ContainsKey(thread))
                    Hooks[thread] = SetWindowsHookEx(WhGetMessage, Marshal.GetFunctionPointerForDelegate(OnMessage), IntPtr.Zero, thread);
            }
            PostMessage(window, WmNull, IntPtr.Zero, IntPtr.Zero); // something for its thread to read
        }
        return Found.Count;
    }

    /// <summary>Removes the hooks, all of them or those whose windows are done.</summary>
    internal static void Unhook(bool finishedOnly = false)
    {
        lock (Gate)
        {
            Waiting.RemoveWhere(w => !IsWindow(w)); // gone before its thread got to it
            if (finishedOnly && Waiting.Count > 0) return;
            foreach (var hook in Hooks.Values) UnhookWindowsHookEx(hook);
            Hooks.Clear();
            Waiting.Clear();
        }
    }

    private static int OnWindow(IntPtr window, IntPtr data)
    {
        if (GetProp(window, DropTargetProperty) != IntPtr.Zero) Found.Add(window);
        return 1; // go on
    }

    /// <summary>On the window's own thread (mpv's), as it reads a message: the revoke works there.</summary>
    private static IntPtr OnThreadMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var me = GetCurrentThreadId();
            lock (Gate)
            {
                foreach (var window in Waiting.Where(w => GetWindowThreadProcessId(w, out _) == me).ToList())
                {
                    RevokeDragDrop(window);
                    Waiting.Remove(window);
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }
}
