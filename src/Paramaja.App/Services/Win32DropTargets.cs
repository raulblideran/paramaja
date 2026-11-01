using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paramaja.App.Services;

/// <summary>
/// Lets files dropped on a playing video reach the player on Windows. mpv registers its video
/// window as a drop target whatever its drag-and-drop option says, then refuses the drop (it's
/// "no": the app opens what's dropped), so a drop over the video went nowhere. With that window's
/// registration revoked, Windows looks up its parents for a drop target and finds the player
/// window's (Avalonia's), whose drop handler opens it. Only the thread that registered a window
/// can revoke it (from any other, RevokeDragDrop fails): mpv's, through <see cref="Win32OnThread"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class Win32DropTargets
{
    [LibraryImport("user32.dll", EntryPoint = "GetPropW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetProp(IntPtr window, string name);

    [LibraryImport("ole32.dll")]
    private static partial int RevokeDragDrop(IntPtr window);

    /// <summary>The window property OLE keeps a registered drop target in.</summary>
    internal const string DropTargetProperty = "OleDropTargetInterface";

    /// <summary>Windows whose thread has been asked to revoke them and hasn't yet.</summary>
    private static readonly HashSet<IntPtr> Asked = [];

    /// <summary>
    /// Revokes the drop targets registered on windows inside <paramref name="host"/> (all its
    /// descendants), and returns how many there were: the UI thread's at once, others as their
    /// thread next reads a message. mpv makes its window again with each video output, so the view
    /// calls this regularly; a window without a drop target costs a property lookup.
    /// </summary>
    public static int RevokeInside(IntPtr host)
    {
        var found = 0;
        foreach (var window in Win32OnThread.ChildrenOf(host).Where(w => GetProp(w, DropTargetProperty) != IntPtr.Zero))
        {
            found++;
            lock (Asked)
                if (!Asked.Add(window)) continue;
            Win32OnThread.Run(window, () =>
            {
                RevokeDragDrop(window);
                lock (Asked) Asked.Remove(window);
            });
        }
        return found;
    }
}
