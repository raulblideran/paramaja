using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Paramaja.App.Services;

/// <param name="Description">The adapter's name as DXGI (and mpv's d3d11-adapter option) knows it.</param>
/// <param name="DefaultOrder">Its place in Windows' own order: 0 is the adapter apps get by default.</param>
public sealed record GpuAdapter(string Description, uint VendorId, long DedicatedVideoMemory, int DefaultOrder);

/// <summary>
/// The graphics adapters, high-performance first, as DXGI ranks them. On a laptop with two GPUs
/// Windows gives apps the power-saving one by default (adapter 0, which drives the built-in
/// screen); games ask for the high-performance one, and so does Paramaja: decoding and scaling
/// 1080p and 4K at a high refresh rate on the integrated GPU made playback lag.
/// </summary>
public static partial class GpuAdapters
{
    /// <summary>Hardware adapters, high-performance first; empty off Windows or if DXGI can't tell.</summary>
    public static IReadOnlyList<GpuAdapter> List()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try { return Dxgi.Adapters(); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException) { return []; }
    }

    /// <summary>
    /// The adapter mpv should render (and decode) on: the high-performance one, where there are
    /// two and Windows wouldn't pick it by default. Null leaves mpv on Windows' default.
    /// </summary>
    public static string? PreferredD3D11Adapter() => Preferred(List());

    internal static string? Preferred(IReadOnlyList<GpuAdapter> adapters) =>
        adapters.Count >= 2 && adapters[0].DefaultOrder != 0 ? adapters[0].Description : null;

    public static string Describe(IReadOnlyList<GpuAdapter> adapters) =>
        adapters.Count == 0 ? "n/a"
        : string.Join("; ", adapters.Select((a, i) =>
            $"{a.Description} ({a.DedicatedVideoMemory / (1024 * 1024)} MB{(i == 0 ? ", high-performance" : "")}{(a.DefaultOrder == 0 ? ", Windows' default" : "")})"));

    /// <summary>Plain COM vtable calls, like <see cref="DxgiHdrProbe"/>: no DirectX wrapper library needed.</summary>
    [SupportedOSPlatform("windows")]
    private static unsafe partial class Dxgi
    {
        private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IidFactory6 = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
        private static readonly Guid IidAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
        private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
        private const uint FlagRemote = 1, FlagSoftware = 2;
        private const uint MicrosoftVendorId = 0x1414;
        private const int GpuPreferenceHighPerformance = 2;

        // vtable slots: IUnknown 0-2, IDXGIObject 3-6, then each interface's own methods.
        private const int SlotQueryInterface = 0, SlotRelease = 2;
        private const int SlotEnumAdapters1 = 12;            // IDXGIFactory1
        private const int SlotEnumAdapterByGpuPreference = 29; // IDXGIFactory6
        private const int SlotGetDesc1 = 10;                 // IDXGIAdapter1

        [StructLayout(LayoutKind.Sequential)]
        private struct AdapterDesc1
        {
            public fixed char Description[128];
            public uint VendorId, DeviceId, SubSysId, Revision;
            public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }

        [LibraryImport("dxgi.dll")]
        private static partial int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

        private static IntPtr Slot(IntPtr com, int slot) => (*(IntPtr**)com)[slot];

        private static void Release(IntPtr com)
        {
            if (com != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, SlotRelease))(com);
        }

        public static List<GpuAdapter> Adapters()
        {
            if (CreateDXGIFactory1(IidFactory1, out var factory) < 0) return [];
            try
            {
                // Windows' own order (adapter 0 is what an app gets by default), by LUID.
                var defaults = new List<(uint, int)>();
                for (uint i = 0; ; i++)
                {
                    IntPtr adapter;
                    var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(factory, SlotEnumAdapters1))(factory, i, &adapter);
                    if (hr == DxgiErrorNotFound || hr < 0) break;
                    var d = Desc(adapter);
                    Release(adapter);
                    if (d is { } desc) defaults.Add((desc.LuidLow, desc.LuidHigh));
                }

                IntPtr factory6;
                var iid6 = IidFactory6;
                if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(factory, SlotQueryInterface))(factory, &iid6, &factory6) < 0)
                    return []; // before Windows 10 1803 there's no GPU preference to ask for
                try
                {
                    var list = new List<GpuAdapter>();
                    var iidAdapter = IidAdapter1;
                    for (uint i = 0; ; i++)
                    {
                        IntPtr adapter;
                        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, Guid*, IntPtr*, int>)Slot(factory6, SlotEnumAdapterByGpuPreference))(
                            factory6, i, GpuPreferenceHighPerformance, &iidAdapter, &adapter);
                        if (hr == DxgiErrorNotFound || hr < 0) break;
                        var d = Desc(adapter);
                        Release(adapter);
                        // Microsoft's own adapters (the Basic Render Driver, a VM's basic display, a
                        // remote session's) are never the one to play on, flagged as software or not.
                        if (d is not { } desc || (desc.Flags & (FlagSoftware | FlagRemote)) != 0 || desc.VendorId == MicrosoftVendorId) continue;
                        list.Add(new GpuAdapter(new string(desc.Description).TrimEnd('\0'), desc.VendorId,
                            (long)desc.DedicatedVideoMemory, defaults.IndexOf((desc.LuidLow, desc.LuidHigh))));
                    }
                    return list;
                }
                finally { Release(factory6); }
            }
            finally { Release(factory); }
        }

        private static AdapterDesc1? Desc(IntPtr adapter)
        {
            AdapterDesc1 desc;
            return ((delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)Slot(adapter, SlotGetDesc1))(adapter, &desc) >= 0 ? desc : null;
        }
    }
}
