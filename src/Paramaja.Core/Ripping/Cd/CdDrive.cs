namespace Paramaja.Core.Ripping.Cd;

/// <summary>A sector (or several) the drive couldn't read.</summary>
public sealed class CdReadException(string message) : IOException(message);

/// <summary>
/// An Audio CD in a drive, as a ripper needs it: what the drive is, the disc's TOC and texts,
/// and its sectors with or without their Q sub-channel.
/// </summary>
public interface ICdDrive : IDisposable
{
    DriveIdentity Identity { get; }
    DriveFeatures Features { get; }
    CdToc ReadToc();
    CdText? ReadCdText();
    string? ReadMcn();
    string? ReadIsrc(int track);
    /// <summary>Reads 2352-byte audio sectors. Throws <see cref="CdReadException"/> on a read error.</summary>
    void ReadAudio(int lba, int sectors, Span<byte> buffer);
    /// <summary>The Q sub-channel position of a sector; null where the drive can't tell (or the frame is the MCN's or an ISRC's).</summary>
    SubQ? ReadSubQ(int lba);
    /// <summary>The drive reads sub-channel data (needed to find the gaps between tracks).</summary>
    bool CanReadSubQ { get; }
}

/// <summary>A SCSI command's outcome: the bytes it moved, or the drive's sense data or the system's error.</summary>
public readonly record struct ScsiResult(bool Ok, int SenseKey = 0, int Asc = 0, int Ascq = 0, int OsError = 0)
{
    public static readonly ScsiResult Success = new(true);

    public override string ToString() => Ok ? "ok" : OsError != 0 ? $"system error {OsError}" : $"sense {SenseKey:X}/{Asc:X2}/{Ascq:X2}";
}

/// <summary>Sends SCSI commands to a drive: SG_IO on Linux, SPTI on Windows.</summary>
public interface IScsiTransport : IDisposable
{
    /// <param name="data">Where the answer goes (commands here only read from the drive).</param>
    ScsiResult Execute(ReadOnlySpan<byte> cdb, Span<byte> data, int timeoutMs = 30_000);
}

/// <summary>A CD drive driven with MMC commands over a <see cref="IScsiTransport"/>.</summary>
public class MmcDrive : ICdDrive
{
    private readonly IScsiTransport _scsi;
    private bool? _subQ;

    public MmcDrive(IScsiTransport scsi)
    {
        _scsi = scsi;
        var inquiry = new byte[96];
        Identity = _scsi.Execute(Mmc.Inquiry(inquiry.Length), inquiry).Ok ? Mmc.ParseInquiry(inquiry) : new DriveIdentity("", "", "");
        var page = new byte[256];
        Features = _scsi.Execute(Mmc.ModeSenseCapabilities(page.Length), page).Ok ? Mmc.ParseCapabilities(page) : DriveFeatures.Unknown;
    }

    public DriveIdentity Identity { get; }
    public DriveFeatures Features { get; }

    public CdToc ReadToc()
    {
        var data = new byte[804]; // 4 + 100 × 8
        var r = _scsi.Execute(Mmc.ReadToc(0, data.Length), data);
        if (!r.Ok) throw new CdReadException($"the drive couldn't read the disc's TOC ({r})");
        return Mmc.ParseToc(data);
    }

    public CdText? ReadCdText()
    {
        var data = new byte[4 + 18 * 255 * 8];
        return _scsi.Execute(Mmc.ReadToc(5, Math.Min(data.Length, 0xFFFE)), data).Ok ? Mmc.ParseCdText(data) : null;
    }

    public string? ReadMcn()
    {
        var data = new byte[48];
        return _scsi.Execute(Mmc.ReadSubChannel(2, 0, data.Length), data).Ok ? Mmc.ParseMcn(data) : null;
    }

    public string? ReadIsrc(int track)
    {
        var data = new byte[48];
        return _scsi.Execute(Mmc.ReadSubChannel(3, track, data.Length), data).Ok ? Mmc.ParseIsrc(data) : null;
    }

    public virtual void ReadAudio(int lba, int sectors, Span<byte> buffer)
    {
        var r = _scsi.Execute(Mmc.ReadCd(lba, sectors), buffer[..(sectors * CdToc.SectorBytes)]);
        if (!r.Ok) throw new CdReadException($"sectors {lba}–{lba + sectors - 1}: {r}");
    }

    public bool CanReadSubQ => _subQ ??= ProbeSubQ();

    private bool ProbeSubQ()
    {
        var data = new byte[CdToc.SectorBytes + Mmc.SubQBytes];
        return _scsi.Execute(Mmc.ReadCd(0, 1, subQ: true), data).Ok;
    }

    public SubQ? ReadSubQ(int lba)
    {
        var data = new byte[CdToc.SectorBytes + Mmc.SubQBytes];
        return _scsi.Execute(Mmc.ReadCd(lba, 1, subQ: true), data).Ok ? Mmc.ParseSubQ(data.AsSpan(CdToc.SectorBytes)) : null;
    }

    public void Dispose()
    {
        _scsi.Dispose();
        GC.SuppressFinalize(this);
    }
}

public static class CdDrives
{
    /// <summary>
    /// Opens a drive for ripping: "D:\" (or "D:") on Windows, "/dev/sr0" on Linux. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if it can't be opened.
    /// </summary>
    public static ICdDrive Open(string drive)
    {
        if (OperatingSystem.IsWindows()) return new MmcDrive(WindowsScsi.Open(drive));
        if (OperatingSystem.IsLinux()) return new MmcDrive(LinuxSgIo.Open(drive));
        throw new PlatformNotSupportedException("CD ripping needs Windows or Linux");
    }
}
