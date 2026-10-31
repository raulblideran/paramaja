using System.Runtime.InteropServices;

namespace Paramaja.App.Services;

/// <summary>
/// Hides the pointer over the whole player on Windows, the engines' video included. The video is a
/// window of mpv's (or libVLC's) own thread, which Avalonia's cursor doesn't reach, and mpv hid the
/// pointer there only if it happened to be tracking the mouse at that moment: in fullscreen it
/// stayed. A child window made by another thread shares its parent's input state, so the display
/// count ShowCursor keeps for the UI thread covers it too; other apps' windows aren't affected.
/// </summary>
public static partial class Win32Pointer
{
    [LibraryImport("user32.dll")]
    private static partial int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetCursor(IntPtr cursor);

    private static bool _hidden;

    /// <summary>On the UI thread: the count is its input state's. Does nothing outside Windows.</summary>
    public static void SetHidden(bool hidden)
    {
        if (!OperatingSystem.IsWindows() || hidden == _hidden) return;
        _hidden = hidden;
        // Each call moves the count by one and returns it; the pointer shows while it's 0 or more.
        // (A few calls at most: something else may have moved it too.)
        if (hidden)
        {
            for (var i = 0; i < 8 && ShowCursor(false) >= 0; i++) { }
            SetCursor(IntPtr.Zero);
        }
        else
        {
            for (var i = 0; i < 8 && ShowCursor(true) < 0; i++) { }
        }
    }
}
