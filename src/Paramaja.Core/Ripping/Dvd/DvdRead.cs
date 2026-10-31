using System.Runtime.InteropServices;
using Paramaja.Core.Native;

namespace Paramaja.Core.Ripping.Dvd;

/// <summary>
/// libdvdread, which reads a DVD's files decrypted: it hands store-bought (CSS) discs to
/// libdvdcss, which authenticates with the drive and works out the keys. Only calls taking ints,
/// sizes and pointers are used, so nothing here depends on how the library was built (off_t).
/// </summary>
public sealed partial class DvdRead : IDisposable
{
    public enum Domain { InfoFile = 0, InfoBackupFile = 1, MenuVobs = 2, TitleVobs = 3 }

    public const int BlockBytes = 2048;

    [LibraryImport(NativeLibraries.DvdRead, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr DVDOpen(string path);
    [LibraryImport(NativeLibraries.DvdRead)] private static partial void DVDClose(IntPtr dvd);
    [LibraryImport(NativeLibraries.DvdRead)] private static partial IntPtr DVDOpenFile(IntPtr dvd, int title, Domain domain);
    [LibraryImport(NativeLibraries.DvdRead)] private static partial void DVDCloseFile(IntPtr file);
    [LibraryImport(NativeLibraries.DvdRead)] private static unsafe partial nint DVDReadBlocks(IntPtr file, int offset, nuint blocks, byte* data);
    [LibraryImport(NativeLibraries.DvdRead)] private static unsafe partial nint DVDReadBytes(IntPtr file, byte* data, nuint bytes);
    [LibraryImport(NativeLibraries.DvdRead)] private static partial nint DVDFileSize(IntPtr file);

    static DvdRead() => NativeLibraries.Register();

    private IntPtr _dvd;

    private DvdRead(IntPtr dvd) => _dvd = dvd;

    /// <summary>libdvdread is there to load.</summary>
    public static bool Available
    {
        get
        {
            try
            {
                Marshal.Prelink(typeof(DvdRead).GetMethod(nameof(DVDOpen), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
            {
                return false;
            }
        }
    }

    /// <summary>A drive ("D:", "/dev/sr0"), an image or a folder. Throws <see cref="IOException"/> if it can't be opened.</summary>
    public static DvdRead Open(string path)
    {
        var dvd = DVDOpen(path);
        if (dvd == IntPtr.Zero) throw new IOException($"libdvdread couldn't open {path}");
        return new DvdRead(dvd);
    }

    public File OpenFile(int titleSet, Domain domain)
    {
        var f = DVDOpenFile(_dvd, titleSet, domain);
        if (f == IntPtr.Zero) throw new IOException($"the disc has no {domain} for title set {titleSet}");
        return new File(f);
    }

    public void Dispose()
    {
        if (_dvd != IntPtr.Zero) DVDClose(_dvd);
        _dvd = IntPtr.Zero;
    }

    /// <summary>One of the disc's files (an IFO, its backup, a set's menu VOB, or a set's title VOBs as one).</summary>
    public sealed class File : IDisposable
    {
        private IntPtr _file;

        internal File(IntPtr file) => _file = file;

        /// <summary>In 2048-byte blocks.</summary>
        public long Blocks => DVDFileSize(_file);

        /// <summary>Reads (and decrypts) blocks; the count read, which may be short or negative on a read error.</summary>
        public unsafe long ReadBlocks(int offset, Span<byte> buffer)
        {
            fixed (byte* p = buffer) return DVDReadBlocks(_file, offset, (nuint)(buffer.Length / BlockBytes), p);
        }

        /// <summary>An IFO or BUP's bytes, from where the last read left off.</summary>
        public unsafe long ReadBytes(Span<byte> buffer)
        {
            fixed (byte* p = buffer) return DVDReadBytes(_file, p, (nuint)buffer.Length);
        }

        public void Dispose()
        {
            if (_file != IntPtr.Zero) DVDCloseFile(_file);
            _file = IntPtr.Zero;
        }
    }
}
