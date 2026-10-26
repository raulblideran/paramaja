using System.Runtime.InteropServices;
using Paramaja.Core.Native;

namespace Paramaja.Core.Torrent;

internal static partial class TorrentNative
{
    static TorrentNative() => NativeLibraries.Register();

    [LibraryImport(NativeLibraries.Torrent, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr nt_init(string configJson);

    [LibraryImport(NativeLibraries.Torrent, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr nt_add(string magnet, ulong timeoutMs);

    [LibraryImport(NativeLibraries.Torrent)]
    public static partial IntPtr nt_select(nuint id, nuint file);

    [LibraryImport(NativeLibraries.Torrent)]
    public static partial IntPtr nt_stats(nuint id);

    [LibraryImport(NativeLibraries.Torrent)]
    public static partial IntPtr nt_remove(nuint id, [MarshalAs(UnmanagedType.U1)] bool deleteFiles);

    [LibraryImport(NativeLibraries.Torrent)]
    public static partial IntPtr nt_shutdown();

    [LibraryImport(NativeLibraries.Torrent)]
    public static partial void nt_free_string(IntPtr s);

    /// <summary>Takes ownership of a returned string, copies it and frees the native buffer.</summary>
    public static string Take(IntPtr p)
    {
        if (p == IntPtr.Zero) throw new TorrentException("paramaja-torrent returned null");
        try { return Marshal.PtrToStringUTF8(p) ?? ""; }
        finally { nt_free_string(p); }
    }
}
