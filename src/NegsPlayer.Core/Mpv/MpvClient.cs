using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;

namespace NegsPlayer.Core.Mpv;

public sealed class MpvException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;
}

public enum EndFileReason { Eof = 0, Stop = 2, Quit = 3, Error = 4, Redirect = 5 }

public sealed record MpvLogMessage(string Prefix, string Level, string Text);

/// <summary>
/// Thin managed wrapper over a libmpv handle. Events are raised on a dedicated
/// background thread — UI code must marshal to its own thread.
/// </summary>
public sealed class MpvClient : IDisposable
{
    private IntPtr _handle;
    private Thread? _eventThread;
    private volatile bool _disposing;
    private ulong _nextObserveId;
    private readonly ConcurrentDictionary<ulong, string> _observed = new();

    public event Action<string, object?>? PropertyChanged;
    public event Action? FileLoaded;
    public event Action? StartFile;
    public event Action<EndFileReason, int>? EndFile;
    public event Action? PlaybackRestart;
    public event Action? VideoReconfig;
    public event Action? AudioReconfig;
    public event Action<string[]>? ClientMessage;
    public event Action<MpvLogMessage>? LogMessage;
    public event Action? Shutdown;

    public bool IsInitialized { get; private set; }

    public MpvClient()
    {
        _handle = MpvNative.mpv_create();
        if (_handle == IntPtr.Zero) throw new MpvException("mpv_create failed");
    }

    public static ulong ApiVersion => MpvNative.mpv_client_api_version();

    public static string ErrorString(int code) =>
        Marshal.PtrToStringUTF8(MpvNative.mpv_error_string(code)) ?? $"mpv error {code}";

    private static void Check(int code, string what)
    {
        if (code < 0) throw new MpvException($"{what}: {ErrorString(code)}", code);
    }

    private IntPtr Handle => _handle != IntPtr.Zero ? _handle : throw new ObjectDisposedException(nameof(MpvClient));

    /// <summary>Sets an option before (or, for most options, after) initialization.</summary>
    public void SetOption(string name, string value) =>
        Check(MpvNative.mpv_set_option_string(Handle, name, value), $"option {name}={value}");

    public void Initialize(string logLevel = "warn")
    {
        Check(MpvNative.mpv_initialize(Handle), "mpv_initialize");
        MpvNative.mpv_request_log_messages(Handle, logLevel);
        IsInitialized = true;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    public void Command(params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            unsafe
            {
                fixed (IntPtr* p = ptrs)
                    Check(MpvNative.mpv_command(Handle, (IntPtr)p), $"command {string.Join(' ', args)}");
            }
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>Like <see cref="Command"/> but returns false instead of throwing.</summary>
    public bool TryCommand(params string[] args)
    {
        try { Command(args); return true; }
        catch (MpvException) { return false; }
    }

    public void SetProperty(string name, string value) =>
        Check(MpvNative.mpv_set_property_string(Handle, name, value), $"property {name}={value}");

    public void SetProperty(string name, double value) =>
        SetProperty(name, value.ToString(CultureInfo.InvariantCulture));

    public void SetProperty(string name, bool value) => SetProperty(name, value ? "yes" : "no");

    public string? GetString(string name)
    {
        var p = MpvNative.mpv_get_property_string(Handle, name);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { MpvNative.mpv_free(p); }
    }

    public unsafe double? GetDouble(string name)
    {
        double v;
        return MpvNative.mpv_get_property(Handle, name, MpvFormat.Double, (IntPtr)(&v)) >= 0 ? v : null;
    }

    public unsafe long? GetLong(string name)
    {
        long v;
        return MpvNative.mpv_get_property(Handle, name, MpvFormat.Int64, (IntPtr)(&v)) >= 0 ? v : null;
    }

    public unsafe bool? GetFlag(string name)
    {
        int v;
        return MpvNative.mpv_get_property(Handle, name, MpvFormat.Flag, (IntPtr)(&v)) >= 0 ? v != 0 : null;
    }

    public void ObserveDouble(string name) => Observe(name, MpvFormat.Double);
    public void ObserveFlag(string name) => Observe(name, MpvFormat.Flag);
    public void ObserveLong(string name) => Observe(name, MpvFormat.Int64);
    public void ObserveString(string name) => Observe(name, MpvFormat.String);

    private void Observe(string name, MpvFormat format)
    {
        var id = Interlocked.Increment(ref _nextObserveId);
        _observed[id] = name;
        Check(MpvNative.mpv_observe_property(Handle, id, name, format), $"observe {name}");
    }

    private void EventLoop()
    {
        var handle = _handle; // Dispose zeroes the field before waking us; keep our own copy.
        while (!_disposing)
        {
            var evPtr = MpvNative.mpv_wait_event(handle, -1);
            if (_disposing) break;
            var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
            try
            {
                if (!Dispatch(ev)) break;
            }
            catch (Exception)
            {
                // A faulty subscriber must not kill the event loop.
            }
        }
    }

    private bool Dispatch(MpvEvent ev)
    {
        switch (ev.EventId)
        {
            case MpvEventId.Shutdown:
                Shutdown?.Invoke();
                return false;
            case MpvEventId.PropertyChange:
                var prop = Marshal.PtrToStructure<MpvEventProperty>(ev.Data);
                var name = Marshal.PtrToStringUTF8(prop.Name) ?? "";
                PropertyChanged?.Invoke(name, ReadValue(prop.Format, prop.Data));
                break;
            case MpvEventId.FileLoaded: FileLoaded?.Invoke(); break;
            case MpvEventId.StartFile: StartFile?.Invoke(); break;
            case MpvEventId.EndFile:
                var end = Marshal.PtrToStructure<MpvEventEndFile>(ev.Data);
                EndFile?.Invoke((EndFileReason)end.Reason, end.Error);
                break;
            case MpvEventId.PlaybackRestart: PlaybackRestart?.Invoke(); break;
            case MpvEventId.VideoReconfig: VideoReconfig?.Invoke(); break;
            case MpvEventId.AudioReconfig: AudioReconfig?.Invoke(); break;
            case MpvEventId.ClientMessage:
                var msg = Marshal.PtrToStructure<MpvEventClientMessage>(ev.Data);
                var args = new string[msg.NumArgs];
                for (var i = 0; i < msg.NumArgs; i++)
                    args[i] = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(msg.Args, i * IntPtr.Size)) ?? "";
                ClientMessage?.Invoke(args);
                break;
            case MpvEventId.LogMessage:
                var log = Marshal.PtrToStructure<MpvEventLogMessage>(ev.Data);
                LogMessage?.Invoke(new MpvLogMessage(
                    Marshal.PtrToStringUTF8(log.Prefix) ?? "",
                    Marshal.PtrToStringUTF8(log.Level) ?? "",
                    (Marshal.PtrToStringUTF8(log.Text) ?? "").TrimEnd('\n')));
                break;
        }
        return true;
    }

    private static object? ReadValue(MpvFormat format, IntPtr data)
    {
        if (data == IntPtr.Zero) return null;
        return format switch
        {
            MpvFormat.Double => Marshal.PtrToStructure<double>(data),
            MpvFormat.Flag => Marshal.ReadInt32(data) != 0,
            MpvFormat.Int64 => Marshal.ReadInt64(data),
            MpvFormat.String or MpvFormat.OsdString => Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(data)),
            _ => null,
        };
    }

    public void Dispose()
    {
        _disposing = true;
        var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h == IntPtr.Zero) return;
        MpvNative.mpv_wakeup(h);
        _eventThread?.Join(TimeSpan.FromSeconds(2));
        MpvNative.mpv_terminate_destroy(h);
    }
}

public static class MpvClientExtensions
{
    public static bool TrySetOption(this MpvClient mpv, string name, string value)
    {
        try { mpv.SetOption(name, value); return true; }
        catch (MpvException) { return false; }
    }
}
