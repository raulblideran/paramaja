using System.Runtime.InteropServices;
using Paramaja.Core.Native;

namespace Paramaja.Core.Mpv;

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public int Reason;
    public int Error;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventClientMessage
{
    public int NumArgs;
    public IntPtr Args;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventHook
{
    public IntPtr Name;
    public ulong Id;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public IntPtr Prefix;
    public IntPtr Level;
    public IntPtr Text;
    public int LogLevel;
}

internal static partial class MpvNative
{
    static MpvNative() => NativeLibraries.Register();

    [LibraryImport(NativeLibraries.Mpv)]
    public static partial ulong mpv_client_api_version();

    [LibraryImport(NativeLibraries.Mpv)]
    public static partial IntPtr mpv_create();

    [LibraryImport(NativeLibraries.Mpv)]
    public static partial int mpv_initialize(IntPtr ctx);

    [LibraryImport(NativeLibraries.Mpv)]
    public static partial void mpv_terminate_destroy(IntPtr ctx);

    [LibraryImport(NativeLibraries.Mpv, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_option_string(IntPtr ctx, string name, string data);

    [LibraryImport(NativeLibraries.Mpv, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_property_string(IntPtr ctx, string name, string data);

    [LibraryImport(NativeLibraries.Mpv, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr mpv_get_property_string(IntPtr ctx, string name);

    [LibraryImport(NativeLibraries.Mpv, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_get_property(IntPtr ctx, string name, MpvFormat format, IntPtr data);

    [LibraryImport(NativeLibraries.Mpv)]
    public static partial int mpv_command(IntPtr ctx, IntPtr args);

    [LibraryImport(NativeLibraries.Mpv, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_observe_property(IntPtr ctx, ulong replyUserdata, string name, MpvFormat format);

    [LibraryImport(NativeLibraries.Mpv, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_request_log_messages(IntPtr ctx, string minLevel);

    [LibraryImport(NativeLibraries.Mpv, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_hook_add(IntPtr ctx, ulong replyUserdata, string name, int priority);

    [LibraryImport(NativeLibraries.Mpv)]
    public static partial int mpv_hook_continue(IntPtr ctx, ulong id);

    [LibraryImport(NativeLibraries.Mpv)]
    public static partial IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [LibraryImport(NativeLibraries.Mpv)]
    public static partial void mpv_wakeup(IntPtr ctx);

    [LibraryImport(NativeLibraries.Mpv)]
    public static partial void mpv_free(IntPtr data);

    [LibraryImport(NativeLibraries.Mpv)]
    public static partial IntPtr mpv_error_string(int error);

    /// <summary>A protocol of our own (sacd://): mpv calls <paramref name="openFn"/> to read such a URL.</summary>
    [LibraryImport(NativeLibraries.Mpv, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_stream_cb_add_ro(IntPtr ctx, string protocol, IntPtr userData, IntPtr openFn);
}
