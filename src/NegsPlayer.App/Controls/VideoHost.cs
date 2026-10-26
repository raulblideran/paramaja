using Avalonia.Controls;
using Avalonia.Platform;

namespace NegsPlayer.App.Controls;

/// <summary>
/// Hosts a native child window (HWND on Windows, X11 window on Linux) that mpv renders into
/// via <c>--wid</c>. Rendering this way keeps mpv's full gpu-next + D3D11 pipeline, so HDR
/// tonemapping and zero-copy hardware decoding behave exactly as in standalone mpv.
/// </summary>
public sealed class VideoHost : NativeControlHost
{
    public event Action<long>? HandleCreated;
    public long? Handle { get; private set; }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        Handle = handle.Handle.ToInt64();
        HandleCreated?.Invoke(Handle.Value);
        return handle;
    }
}
