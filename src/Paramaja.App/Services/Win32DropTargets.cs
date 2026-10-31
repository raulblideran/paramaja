using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paramaja.App.Services;

/// <summary>
/// Lets files dropped on a playing video reach the player on Windows. mpv registers its video
/// window as a drop target whatever its drag-and-drop option says, then refuses the drop (it's
/// "no": the app opens what's dropped), so a drop over the video went nowhere. With that window's
/// registration revoked, Windows looks up its parents for a drop target and finds the player
/// window's (Avalonia's), whose drop handler opens it.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class Win32DropTargets
{
    private delegate int EnumProc(IntPtr window, IntPtr data);

    [LibraryImport("user32.dll")]
    private static partial int EnumChildWindows(IntPtr parent, IntPtr proc, IntPtr data);

    [LibraryImport("user32.dll", EntryPoint = "GetPropW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetProp(IntPtr window, string name);

    [LibraryImport("ole32.dll")]
    private static partial int RevokeDragDrop(IntPtr window);

    /// <summary>The window property OLE keeps a registered drop target in.</summary>
    internal const string DropTargetProperty = "OleDropTargetInterface";

    private static readonly EnumProc Visit = OnWindow; // kept alive: Windows only holds a raw pointer to it
    private static int _revoked;

    /// <summary>
    /// Revokes the drop targets registered on windows inside <paramref name="host"/> (all its
    /// descendants), and returns how many there were. mpv makes its window again with each video
    /// output, so the view calls this regularly; a window without one costs a property lookup.
    /// </summary>
    public static int RevokeInside(IntPtr host)
    {
        if (host == IntPtr.Zero) return 0;
        _revoked = 0;
        EnumChildWindows(host, Marshal.GetFunctionPointerForDelegate(Visit), IntPtr.Zero);
        return _revoked;
    }

    private static int OnWindow(IntPtr window, IntPtr data)
    {
        if (GetProp(window, DropTargetProperty) != IntPtr.Zero && RevokeDragDrop(window) == 0) _revoked++;
        return 1; // go on
    }
}
