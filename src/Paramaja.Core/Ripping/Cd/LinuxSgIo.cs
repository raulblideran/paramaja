using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Paramaja.Core.Ripping.Cd;

/// <summary>
/// SCSI commands to a Linux CD drive (/dev/sr0) through the SG_IO ioctl. Opened read-only, which
/// is enough for the commands a ripper sends (READ CD, READ TOC, INQUIRY, MODE SENSE, READ
/// SUB-CHANNEL are on the kernel's list for readers). Where SG_IO is refused, audio sectors and
/// the TOC come through the older CD-ROM ioctls (CDROMREADAUDIO, CDROMREADTOC*).
/// </summary>
public sealed partial class LinuxSgIo : IScsiTransport
{
    private const int ORdOnly = 0, ONonBlock = 0x800;
    private const uint SgIoRequest = 0x2285, CdRomReadTocHdr = 0x5305, CdRomReadTocEntry = 0x5306, CdRomReadAudio = 0x530E;
    private const int SgDxferNone = -1, SgDxferFromDev = -3;
    private const int EPerm = 1, EAcces = 13, EInval = 22, ENotTy = 25;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SgIoHdr
    {
        public int InterfaceId;
        public int DxferDirection;
        public byte CmdLen;
        public byte MxSbLen;
        public ushort IovecCount;
        public uint DxferLen;
        public IntPtr Dxferp;
        public IntPtr Cmdp;
        public IntPtr Sbp;
        public uint Timeout;
        public uint Flags;
        public int PackId;
        public IntPtr UsrPtr;
        public byte Status;
        public byte MaskedStatus;
        public byte MsgStatus;
        public byte SbLenWr;
        public ushort HostStatus;
        public ushort DriverStatus;
        public int Resid;
        public uint Duration;
        public uint Info;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CdromReadAudio
    {
        public int Lba;
        public byte AddrFormat;
        public int Frames;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CdromTocHeader
    {
        public byte First, Last;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CdromTocEntry
    {
        public byte Track;
        public byte AdrCtrl; // adr in the low four bits, control in the high four
        public byte Format;
        public int Lba;
        public byte DataMode;
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "close")] private static partial int Close(int fd);
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static partial int Ioctl(int fd, nuint request, ref SgIoHdr hdr);
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static partial int Ioctl(int fd, nuint request, ref CdromReadAudio ra);
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static partial int Ioctl(int fd, nuint request, ref CdromTocHeader h);
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static partial int Ioctl(int fd, nuint request, ref CdromTocEntry e);

    private int _fd;
    private bool _sgRefused;

    private LinuxSgIo(int fd) => _fd = fd;

    public static LinuxSgIo Open(string device)
    {
        var fd = Open(device, ORdOnly | ONonBlock);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno is EAcces or EPerm) throw new UnauthorizedAccessException($"no permission to read {device}");
            throw new IOException($"can't open {device} (error {errno})");
        }
        return new LinuxSgIo(fd);
    }

    public unsafe ScsiResult Execute(ReadOnlySpan<byte> cdb, Span<byte> data, int timeoutMs = 30_000)
    {
        if (!_sgRefused)
        {
            var sense = stackalloc byte[64];
            fixed (byte* c = cdb)
            fixed (byte* d = data)
            {
                var hdr = new SgIoHdr
                {
                    InterfaceId = 'S',
                    DxferDirection = data.Length > 0 ? SgDxferFromDev : SgDxferNone,
                    CmdLen = (byte)cdb.Length,
                    MxSbLen = 64,
                    DxferLen = (uint)data.Length,
                    Dxferp = (IntPtr)d,
                    Cmdp = (IntPtr)c,
                    Sbp = (IntPtr)sense,
                    Timeout = (uint)timeoutMs,
                };
                if (Ioctl(_fd, SgIoRequest, ref hdr) == 0)
                {
                    if (hdr.Status == 0 && hdr.HostStatus == 0 && (hdr.DriverStatus & 0x0F) == 0) return ScsiResult.Success;
                    return Sense(new ReadOnlySpan<byte>(sense, hdr.SbLenWr), hdr.Status);
                }
                var errno = Marshal.GetLastPInvokeError();
                if (errno is not (EPerm or EAcces or EInval or ENotTy)) return new ScsiResult(false, OsError: errno);
                _sgRefused = true; // this kernel or sandbox won't take SG_IO from us: the old ioctls then
            }
        }
        return Fallback(cdb, data);
    }

    /// <summary>Sense data, fixed (70h/71h) or descriptor (72h/73h) format.</summary>
    internal static ScsiResult Sense(ReadOnlySpan<byte> sense, int status)
    {
        if (sense.Length >= 14 && (sense[0] & 0x7E) == 0x70) return new ScsiResult(false, sense[2] & 0x0F, sense[12], sense[13]);
        if (sense.Length >= 4 && (sense[0] & 0x7E) == 0x72) return new ScsiResult(false, sense[1] & 0x0F, sense[2], sense[3]);
        return new ScsiResult(false, OsError: status == 0 ? -1 : status);
    }

    private unsafe ScsiResult Fallback(ReadOnlySpan<byte> cdb, Span<byte> data)
    {
        switch (cdb[0])
        {
            case Mmc.OpReadCd when cdb[10] == 0:
            {
                var lba = BinaryPrimitives.ReadInt32BigEndian(cdb[2..]);
                var count = (cdb[6] << 16) | (cdb[7] << 8) | cdb[8];
                fixed (byte* d = data)
                {
                    var ra = new CdromReadAudio { Lba = lba, AddrFormat = 1 /* CDROM_LBA */, Frames = count, Buffer = (IntPtr)d };
                    return Ioctl(_fd, CdRomReadAudio, ref ra) == 0 ? ScsiResult.Success : new ScsiResult(false, OsError: Marshal.GetLastPInvokeError());
                }
            }
            case Mmc.OpReadToc when (cdb[2] & 0x0F) == 0:
                return ReadTocIoctl(data);
            default:
                return new ScsiResult(false, OsError: EPerm); // nothing else to fall back on (ISRCs, CD-Text, sub-channel)
        }
    }

    /// <summary>The TOC through CDROMREADTOCHDR/ENTRY, laid out as READ TOC format 0 answers.</summary>
    private ScsiResult ReadTocIoctl(Span<byte> data)
    {
        var header = new CdromTocHeader();
        if (Ioctl(_fd, CdRomReadTocHdr, ref header) != 0) return new ScsiResult(false, OsError: Marshal.GetLastPInvokeError());
        var p = 4;
        foreach (var track in Enumerable.Range(header.First, header.Last - header.First + 1).Append(0xAA))
        {
            var e = new CdromTocEntry { Track = (byte)track, Format = 1 /* CDROM_LBA */ };
            if (Ioctl(_fd, CdRomReadTocEntry, ref e) != 0) return new ScsiResult(false, OsError: Marshal.GetLastPInvokeError());
            if (p + 8 > data.Length) break;
            data[p + 1] = (byte)((e.AdrCtrl << 4) | (e.AdrCtrl >> 4)); // MMC puts ADR high, control low
            data[p + 2] = (byte)track;
            BinaryPrimitives.WriteInt32BigEndian(data[(p + 4)..], e.Lba);
            p += 8;
        }
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)(p - 2));
        data[2] = header.First;
        data[3] = header.Last;
        return ScsiResult.Success;
    }

    public void Dispose()
    {
        if (_fd >= 0) Close(_fd);
        _fd = -1;
    }
}
