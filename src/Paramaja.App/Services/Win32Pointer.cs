using System.Runtime.InteropServices;

namespace Paramaja.App.Services;

/// <summary>
/// Hides the pointer over the whole player on Windows, the engines' video included. The video is a
/// window of mpv's (or libVLC's) own thread, which Avalonia's cursor doesn't reach, and mpv hid the
/// pointer there only if it happened to be tracking the mouse at that moment: in fullscreen it
/// stayed. ShowCursor keeps a display count per thread's input: it's lowered on the UI thread and
/// on each video window's thread (<see cref="Win32OnThread"/>), so it holds over every window of the
/// player whether or not Windows shares one count between them. Other apps aren't affected.
/// </summary>
public static partial class Win32Pointer
{
    [LibraryImport("user32.dll")]
    private static partial int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetCursor(IntPtr cursor);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static partial IntPtr LoadCursor(IntPtr instance, IntPtr name);

    private const int IdcArrow = 32512;

    private static bool _hidden;

    /// <summary>On the UI thread. <paramref name="videoHosts"/>: the windows the engines' video windows are inside. Nothing outside Windows.</summary>
    public static void SetHidden(bool hidden, IEnumerable<IntPtr> videoHosts)
    {
        if (!OperatingSystem.IsWindows() || hidden == _hidden) return;
        _hidden = hidden;
        Apply(hidden);
        var threads = new HashSet<uint>();
        foreach (var window in videoHosts.SelectMany(Win32OnThread.ChildrenOf))
            if (threads.Add(Win32OnThread.ThreadOf(window)) && !Win32OnThread.IsCurrent(Win32OnThread.ThreadOf(window)))
                Win32OnThread.Run(window, () => Apply(hidden));
    }

    /// <summary>On the thread whose count it is.</summary>
    private static void Apply(bool hidden)
    {
        // Each call moves the count by one and returns it; the pointer shows while it's 0 or more.
        // (A few calls at most: something else may have moved it too, or share it.)
        if (hidden)
        {
            for (var i = 0; i < 8 && ShowCursor(false) >= 0; i++) { }
            SetCursor(IntPtr.Zero);
        }
        else
        {
            for (var i = 0; i < 8 && ShowCursor(true) < 0; i++) { }
            // The count alone leaves the null shape set above until a window sets one, and over the
            // video that's the engine's window, which may not: the arrow, now.
            SetCursor(LoadCursor(IntPtr.Zero, IdcArrow));
        }
    }
}
