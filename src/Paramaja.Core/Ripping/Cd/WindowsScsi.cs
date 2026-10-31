using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Paramaja.Core.Ripping.Cd;

/// <summary>
/// SCSI commands to a Windows CD drive (\\.\D:). SPTI (IOCTL_SCSI_PASS_THROUGH_DIRECT) takes any
/// command; it needs the drive opened for reading and writing, which Windows allows users on CD
/// drives. Where that's refused, <see cref="Ioctls"/> answers the same commands from the CD-ROM
/// driver's own requests (TOC, CD-Text, sub-channel, raw audio sectors), with no Q sub-channel
/// per sector: gaps then can't be found.
/// </summary>
public static partial class WindowsScsi
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    internal const uint IoctlScsiPassThroughDirect = 0x4D014;
    internal const uint IoctlCdromReadTocEx = 0x24054, IoctlCdromRawRead = 0x2403E, IoctlCdromReadQChannel = 0x2402C;
    internal const uint IoctlStorageQueryProperty = 0x2D1400;
    private const int ErrorAccessDenied = 5;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, int inputSize,
        void* output, int outputSize, out int returned, IntPtr overlapped);

    /// <summary>"D:", "D:\" or "D" as the device path \\.\D:.</summary>
    internal static string DevicePath(string drive)
    {
        var letter = drive.TrimEnd('\\', '/').TrimEnd(':');
        if (letter.Length != 1 || !char.IsAsciiLetter(letter[0])) throw new ArgumentException($"{drive} isn't a drive letter", nameof(drive));
        return $@"\\.\{char.ToUpperInvariant(letter[0])}:";
    }

    public static IScsiTransport Open(string drive)
    {
        var path = DevicePath(drive);
        var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (!handle.IsInvalid) return new Spti(handle);
        var error = Marshal.GetLastPInvokeError();
        if (error != ErrorAccessDenied) throw new IOException($"can't open {path}: {new Win32Exception(error).Message}");
        handle = CreateFile(path, GenericRead, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid) throw new UnauthorizedAccessException($"can't open {path}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        return new Ioctls(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ScsiPassThroughDirect
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        public unsafe fixed byte Cdb[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ScsiPassThroughDirectWithSense
    {
        public ScsiPassThroughDirect Spt;
        public uint Filler;
        public unsafe fixed byte Sense[32];
    }

    internal sealed class Spti(SafeFileHandle handle) : IScsiTransport
    {
        public unsafe ScsiResult Execute(ReadOnlySpan<byte> cdb, Span<byte> data, int timeoutMs = 30_000)
        {
            // The adapter may need the buffer aligned: a page-aligned copy keeps every adapter happy.
            var buffer = data.Length > 0 ? NativeMemory.AlignedAlloc((nuint)data.Length, 4096) : null;
            try
            {
                var s = new ScsiPassThroughDirectWithSense();
                s.Spt.Length = (ushort)sizeof(ScsiPassThroughDirect);
                s.Spt.CdbLength = (byte)cdb.Length;
                s.Spt.SenseInfoLength = 32;
                s.Spt.DataIn = 1; // SCSI_IOCTL_DATA_IN
                s.Spt.DataTransferLength = (uint)data.Length;
                s.Spt.TimeOutValue = (uint)Math.Max(1, timeoutMs / 1000);
                s.Spt.DataBuffer = (IntPtr)buffer;
                s.Spt.SenseInfoOffset = (uint)Marshal.OffsetOf<ScsiPassThroughDirectWithSense>(nameof(ScsiPassThroughDirectWithSense.Sense));
                for (var i = 0; i < cdb.Length && i < 16; i++) s.Spt.Cdb[i] = cdb[i];
                if (!DeviceIoControl(handle, IoctlScsiPassThroughDirect, &s, sizeof(ScsiPassThroughDirectWithSense),
                        &s, sizeof(ScsiPassThroughDirectWithSense), out _, 0))
                    return new ScsiResult(false, OsError: Marshal.GetLastPInvokeError());
                if (s.Spt.ScsiStatus != 0) return LinuxSgIo.Sense(new ReadOnlySpan<byte>(s.Sense, 32), s.Spt.ScsiStatus);
                if (buffer is not null) new ReadOnlySpan<byte>(buffer, data.Length).CopyTo(data);
                return ScsiResult.Success;
            }
            finally
            {
                if (buffer is not null) NativeMemory.AlignedFree(buffer);
            }
        }

        public void Dispose() => handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawReadInfo
    {
        public long DiskOffset;
        public uint SectorCount;
        public int TrackMode; // CDDA = 2
    }

    /// <summary>
    /// The commands a ripper sends, answered by the CD-ROM driver's own requests, for a drive
    /// opened read-only. Their answers are laid out as the MMC commands' are.
    /// </summary>
    internal sealed class Ioctls(SafeFileHandle handle) : IScsiTransport
    {
        public unsafe ScsiResult Execute(ReadOnlySpan<byte> cdb, Span<byte> data, int timeoutMs = 30_000)
        {
            fixed (byte* d = data)
            {
                switch (cdb[0])
                {
                    case Mmc.OpReadToc:
                    {
                        // CDROM_READ_TOC_EX: the format in the low four bits, MSF in the top one (off: LBA).
                        var request = stackalloc byte[4] { (byte)(cdb[2] & 0x0F), 0, 0, 0 };
                        return Io(IoctlCdromReadTocEx, request, 4, d, data.Length);
                    }
                    case Mmc.OpReadSubChannel:
                    {
                        // CDROM_SUB_Q_DATA_FORMAT: the format (2 MCN, 3 ISRC) and the track.
                        var request = stackalloc byte[2] { cdb[3], cdb[6] };
                        return Io(IoctlCdromReadQChannel, request, 2, d, data.Length);
                    }
                    case Mmc.OpReadCd when cdb[10] == 0:
                    {
                        var info = new RawReadInfo
                        {
                            DiskOffset = BinaryPrimitives.ReadInt32BigEndian(cdb[2..]) * 2048L,
                            SectorCount = (uint)((cdb[6] << 16) | (cdb[7] << 8) | cdb[8]),
                            TrackMode = 2,
                        };
                        // As with SPTI, the driver may want the buffer aligned.
                        var aligned = (byte*)NativeMemory.AlignedAlloc((nuint)data.Length, 4096);
                        try
                        {
                            var r = Io(IoctlCdromRawRead, &info, sizeof(RawReadInfo), aligned, data.Length);
                            if (r.Ok) new ReadOnlySpan<byte>(aligned, data.Length).CopyTo(data);
                            return r;
                        }
                        finally
                        {
                            NativeMemory.AlignedFree(aligned);
                        }
                    }
                    case Mmc.OpInquiry:
                        return Inquiry(data);
                    default:
                        return new ScsiResult(false, OsError: ErrorAccessDenied);
                }
            }
        }

        private unsafe ScsiResult Io(uint code, void* input, int inputSize, byte* output, int outputSize) =>
            DeviceIoControl(handle, code, input, inputSize, output, outputSize, out _, 0)
                ? ScsiResult.Success
                : new ScsiResult(false, OsError: Marshal.GetLastPInvokeError());

        /// <summary>The drive's names from STORAGE_DEVICE_DESCRIPTOR, laid out as an INQUIRY answer.</summary>
        private unsafe ScsiResult Inquiry(Span<byte> data)
        {
            var query = stackalloc byte[12]; // STORAGE_PROPERTY_QUERY: StorageDeviceProperty, PropertyStandardQuery
            var descriptor = new byte[1024];
            fixed (byte* o = descriptor)
            {
                if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, 12, o, descriptor.Length, out _, 0))
                    return new ScsiResult(false, OsError: Marshal.GetLastPInvokeError());
            }
            string Text(int offsetField)
            {
                var at = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(offsetField));
                if (at <= 0 || at >= descriptor.Length) return "";
                var end = Array.IndexOf(descriptor, (byte)0, at);
                return System.Text.Encoding.ASCII.GetString(descriptor, at, (end < 0 ? descriptor.Length : end) - at);
            }
            data.Clear();
            if (data.Length >= 36)
            {
                Put(data, Text(12), 8, 8);
                Put(data, Text(16), 16, 16);
                Put(data, Text(20), 32, 4);
            }
            return ScsiResult.Success;
        }

        private static void Put(Span<byte> data, string s, int at, int length) =>
            System.Text.Encoding.ASCII.GetBytes(s.PadRight(length)[..length]).CopyTo(data[at..]);

        public void Dispose() => handle.Dispose();
    }
}
