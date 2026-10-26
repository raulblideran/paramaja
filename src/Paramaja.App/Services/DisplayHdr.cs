using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paramaja.App.Services;

/// <summary>
/// Tells whether the display a window is on is in HDR mode, which decides what HDR "Auto" does.
/// </summary>
public interface IDisplayHdrProbe
{
    bool IsHdrActive(IntPtr window);
}

public static class DisplayHdr
{
    /// <summary>
    /// On Linux the app is an X11 (XWayland) client, which can't output HDR at all, so the
    /// display always counts as SDR there and HDR is tonemapped.
    /// </summary>
    public static IDisplayHdrProbe Create() =>
        OperatingSystem.IsWindows() ? new DxgiHdrProbe() : new NoHdrProbe();
}

internal sealed class NoHdrProbe : IDisplayHdrProbe
{
    public bool IsHdrActive(IntPtr window) => false;
}

/// <summary>
/// Asks DXGI for the colour space of the monitor the window is on: Windows reports
/// BT.2020 PQ for a monitor with "Use HDR" switched on. Plain COM vtable calls, so no
/// DirectX wrapper library is needed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class DxgiHdrProbe : IDisplayHdrProbe
{
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const int ColorSpaceRgbFullG2084P2020 = 12;
    private const uint MonitorDefaultToNearest = 2;

    // vtable slots: IUnknown 0-2, IDXGIObject 3-6, then each interface's own methods.
    private const int SlotQueryInterface = 0, SlotRelease = 2;
    private const int SlotEnumAdapters1 = 12; // IDXGIFactory1
    private const int SlotEnumOutputs = 7;    // IDXGIAdapter
    private const int SlotGetDesc1 = 27;      // IDXGIOutput6

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDesc1
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
        public uint BitsPerColor;
        public int ColorSpace;
        public fixed float Primaries[8];
        public float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
    }

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    private static void** Vtbl(IntPtr com) => *(void***)com;

    private static void Release(IntPtr com)
    {
        if (com != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(com)[SlotRelease])(com);
    }

    public bool IsHdrActive(IntPtr window)
    {
        try { return Probe(window); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or SEHException) { return false; }
    }

    private static bool Probe(IntPtr window)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (CreateDXGIFactory1(IidFactory1, out var factory) < 0) return false;
        try
        {
            for (uint a = 0; ; a++)
            {
                IntPtr adapter;
                var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(factory)[SlotEnumAdapters1])(factory, a, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0) return false;
                try
                {
                    for (uint o = 0; ; o++)
                    {
                        IntPtr output;
                        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(adapter)[SlotEnumOutputs])(adapter, o, &output);
                        if (hr < 0) break;
                        try
                        {
                            if (Describe(output) is { } d && d.Monitor == monitor) return d.ColorSpace == ColorSpaceRgbFullG2084P2020;
                        }
                        finally { Release(output); }
                    }
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
    }

    /// <summary>IDXGIOutput6::GetDesc1; null on Windows versions too old to have it (no HDR either).</summary>
    private static OutputDesc1? Describe(IntPtr output)
    {
        IntPtr output6;
        var iid = IidOutput6;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(output)[SlotQueryInterface])(output, &iid, &output6) < 0)
            return null;
        try
        {
            OutputDesc1 desc;
            return ((delegate* unmanaged[Stdcall]<IntPtr, OutputDesc1*, int>)Vtbl(output6)[SlotGetDesc1])(output6, &desc) < 0 ? null : desc;
        }
        finally { Release(output6); }
    }
}
