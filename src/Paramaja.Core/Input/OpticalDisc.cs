using System.Runtime.InteropServices;

namespace Paramaja.Core.Input;

/// <summary>What a drive holds.</summary>
public enum DiscContent { Unknown, NoDisc, Audio, Data }

/// <summary>
/// Asks an optical drive what's in it, without mounting anything: an unmounted disc may be an
/// Audio CD (which has no file system to mount) or a DVD.
/// </summary>
public static partial class OpticalDisc
{
    // linux/cdrom.h
    private const uint CdromDriveStatus = 0x5326, CdromDiscStatus = 0x5327;
    private const int CdsNoDisc = 1, CdsTrayOpen = 2, CdsDriveNotReady = 3;
    private const int CdsAudio = 100, CdsMixed = 105;
    private const int ORdOnly = 0, ONonBlock = 0x800;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(int fd, nuint request, nint arg);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);

    /// <summary>A Linux drive (/dev/sr0): no disc, an Audio CD (or an enhanced CD with audio first), or data.</summary>
    public static DiscContent Probe(string device)
    {
        if (!OperatingSystem.IsLinux()) return DiscContent.Unknown;
        int fd;
        try { fd = Open(device, ORdOnly | ONonBlock); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return DiscContent.Unknown; }
        if (fd < 0) return DiscContent.Unknown;
        try
        {
            var drive = Ioctl(fd, CdromDriveStatus, 0);
            if (drive is CdsNoDisc or CdsTrayOpen or CdsDriveNotReady) return DiscContent.NoDisc;
            return Ioctl(fd, CdromDiscStatus, 0) switch
            {
                CdsAudio or CdsMixed => DiscContent.Audio,
                CdsNoDisc => DiscContent.NoDisc,
                < 0 => DiscContent.Unknown,
                _ => DiscContent.Data,
            };
        }
        finally
        {
            Close(fd);
        }
    }

    /// <summary>Windows shows an Audio CD as Track01.cda, Track02.cda… at the drive's root.</summary>
    public static bool HasCdaTracks(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*.cda").Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
