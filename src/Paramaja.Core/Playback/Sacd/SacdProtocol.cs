using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Paramaja.Core.Mpv;

namespace Paramaja.Core.Playback.Sacd;

/// <summary>
/// The sacd:// protocol, which mpv reads through us (libmpv's stream callbacks): each URL is one
/// track of an area of an SACD image, served as a DSDIFF file (<see cref="SacdTrackStream"/>).
/// An area plays as an edl:// of its tracks, so they're chapters, as a CD rip's are.
/// </summary>
public static unsafe class SacdProtocol
{
    public const string Scheme = "sacd";

    // libmpv's error codes.
    private const int LoadingFailed = -13, Unsupported = -18, Generic = -20;

    /// <summary>sacd://2ch/3?/path/to/image.iso: the stereo area's third track.</summary>
    public static string Url(string image, SacdAreaKind area, int track) =>
        $"{Scheme}://{(area == SacdAreaKind.Stereo ? "2ch" : "mch")}/{track.ToString(CultureInfo.InvariantCulture)}?{Uri.EscapeDataString(image)}";

    internal static (string Image, SacdAreaKind Area, int Track)? Parse(string url)
    {
        var prefix = Scheme + "://";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var query = url.IndexOf('?');
        if (query < 0) return null;
        var parts = url[prefix.Length..query].Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var track)) return null;
        SacdAreaKind? area = parts[0] switch { "2ch" => SacdAreaKind.Stereo, "mch" => SacdAreaKind.Multichannel, _ => null };
        return area is { } a ? (Uri.UnescapeDataString(url[(query + 1)..]), a, track) : null;
    }

    /// <summary>The area's tracks, one after the other, each a titled chapter.</summary>
    public static string EdlFor(SacdImage image, SacdArea area) =>
        "edl://" + string.Join(';', area.Tracks.Select(t =>
            $"{Escape(Url(image.Path, area.Kind, t.Number))},length={Length(t)},title={Escape(t.Label)}"));

    /// <summary>The track's length, rounded down to the millisecond: a hair over what the file has makes mpv complain.</summary>
    private static string Length(SacdTrack track) =>
        (Math.Floor(track.Seconds * 1000) / 1000).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>EDL's length-prefixed quoting (%bytes%value), which allows any character.</summary>
    private static string Escape(string value) => $"%{Encoding.UTF8.GetByteCount(value)}%{value}";

    /// <summary>Registers sacd:// with an mpv instance; false where this libmpv can't take custom protocols.</summary>
    public static bool Register(MpvClient mpv)
    {
        try
        {
            mpv.AddStreamProtocol(Scheme, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&Open);
            return true;
        }
        catch (Exception e) when (e is MpvException or EntryPointNotFoundException) { return false; }
    }

    // ---- The images opened: read once, while unchanged ------------------------------------------

    private static readonly Dictionary<string, (DateTime Written, SacdImage Image)> Images = new(StringComparer.Ordinal);

    public static SacdImage Image(string path)
    {
        var written = File.GetLastWriteTimeUtc(path);
        lock (Images)
        {
            if (Images.TryGetValue(path, out var known) && known.Written == written) return known.Image;
            var image = SacdImage.Read(path);
            Images[path] = (written, image);
            return image;
        }
    }

    // ---- libmpv's callbacks (on its threads) ------------------------------------------------------

    // mpv_stream_cb_info: cookie, then read, seek, size, close and cancel functions.
    [StructLayout(LayoutKind.Sequential)]
    private struct StreamInfo
    {
        public IntPtr Cookie;
        public IntPtr Read, Seek, Size, Close, Cancel;
    }

    [UnmanagedCallersOnly]
    private static int Open(IntPtr userData, IntPtr uri, IntPtr info)
    {
        try
        {
            if (Parse(Marshal.PtrToStringUTF8(uri) ?? "") is not { } target) return LoadingFailed;
            var image = Image(target.Image);
            if (image.Area(target.Area) is not { } area || target.Track < 1 || target.Track > area.Tracks.Count) return LoadingFailed;
            var file = new FileStream(target.Image, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            var stream = new SacdTrackStream(file, area, area.Tracks[target.Track - 1]);
            *(StreamInfo*)info = new StreamInfo
            {
                Cookie = GCHandle.ToIntPtr(GCHandle.Alloc(stream)),
                Read = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, ulong, long>)&Read,
                Seek = (IntPtr)(delegate* unmanaged<IntPtr, long, long>)&Seek,
                Size = (IntPtr)(delegate* unmanaged<IntPtr, long>)&Size,
                Close = (IntPtr)(delegate* unmanaged<IntPtr, void>)&Close,
            };
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return LoadingFailed; }
    }

    private static SacdTrackStream Stream(IntPtr cookie) => (SacdTrackStream)GCHandle.FromIntPtr(cookie).Target!;

    [UnmanagedCallersOnly]
    private static long Read(IntPtr cookie, IntPtr buffer, ulong count)
    {
        try { return Stream(cookie).Read(new Span<byte>((void*)buffer, (int)Math.Min(count, int.MaxValue))); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException) { return -1; }
    }

    [UnmanagedCallersOnly]
    private static long Seek(IntPtr cookie, long offset)
    {
        try { return Stream(cookie).Position = offset; }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { return Generic; }
    }

    [UnmanagedCallersOnly]
    private static long Size(IntPtr cookie)
    {
        try { return Stream(cookie).KnownLength ?? Unsupported; }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { return Unsupported; }
    }

    [UnmanagedCallersOnly]
    private static void Close(IntPtr cookie)
    {
        var handle = GCHandle.FromIntPtr(cookie);
        (handle.Target as IDisposable)?.Dispose();
        handle.Free();
    }
}
